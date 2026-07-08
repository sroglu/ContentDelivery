using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ContentDelivery.Core
{
    /// <summary>How many bundles a <see cref="DownloadScheduler"/> provisions at once.</summary>
    public enum SchedulerConcurrency
    {
        Sequential = 0, // one bundle at a time — gentlest on a constrained connection
        Balanced = 1,   // up to MaxConcurrency in flight — the default
    }

    /// <summary>Tuning for a <see cref="DownloadScheduler"/> run.</summary>
    public sealed class DownloadSchedulerOptions
    {
        public SchedulerConcurrency Concurrency = SchedulerConcurrency.Balanced;
        public int MaxConcurrency = 4;                 // in-flight bundles when Balanced (also the mem-guard: only this
                                                       // many bundles hold their bytes in memory at once)

        // --- split retry policy: a flaky connection deserves many patient retries, but bytes that keep arriving
        //     corrupt almost never heal, so they get a short, distinct budget (mirrors a long transient budget
        //     vs. a tiny integrity budget). Transient and integrity failures are counted independently. ---
        public int MaxItemRetries = 5;                 // transient/network retries (timeout, reset, 5xx). Set high
                                                       // (e.g. 100) to ride out a long outage.
        public TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(200); // transient base backoff; doubled each retry
        public int MaxIntegrityRetries = 3;            // retries for corrupt bytes (hash mismatch / decompress fail)
        public TimeSpan IntegrityRetryBackoff = TimeSpan.FromSeconds(5); // fixed wait between integrity retries

        // Per-transfer stall watchdog: if a single provision attempt makes no headway within this window it is
        // cancelled and counted as a transient failure (then retried). Zero/negative disables the watchdog.
        public TimeSpan StallTimeout = TimeSpan.Zero;

        public long EstimatedTotalBytes = 0;           // if > 0, a disk-capacity precheck runs before downloading
        public string CacheDirectory;                  // drive checked for free space (required for the disk precheck)

        // Optional: fed the aggregate bytes/sec on every progress tick so it can classify the connection Good/Bad
        // and raise transition events (see DownloadSpeedMeter). Null = no speed classification.
        public DownloadSpeedMeter SpeedMeter;
    }

    /// <summary>Aggregate progress pushed to the caller's <see cref="IProgress{T}"/> as bundles complete.</summary>
    public struct SchedulerProgress
    {
        public int Completed;
        public int Failed;
        public int Total;
        public long BytesProvisioned;
        public double BytesPerSecond;
    }

    /// <summary>Outcome of a scheduler run.</summary>
    public struct SchedulerResult
    {
        public int Succeeded;
        public int Failed;
        public long BytesProvisioned;
        public string[] Paths; // provisioned path per input index (null where that bundle failed)
    }

    /// <summary>
    /// Provisions many bundles through a <see cref="BundleProvisioner"/> under a concurrency policy, layering the
    /// cross-bundle concerns the per-bundle provisioner does not own: a parallelism cap (which doubles as a memory
    /// guard, since only that many bundles hold bytes at once), a whole-bundle retry budget with exponential
    /// backoff on transient failures, aggregate progress + a download speed meter, and an up-front disk-capacity
    /// check. Pure C# (BCL only) — schedulable and testable without Unity.
    /// </summary>
    public sealed class DownloadScheduler
    {
        private readonly BundleProvisioner _provisioner;
        private readonly DownloadSchedulerOptions _options;

        public DownloadScheduler(BundleProvisioner provisioner, DownloadSchedulerOptions options = null)
        {
            _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
            _options = options ?? new DownloadSchedulerOptions();
        }

        public async Task<SchedulerResult> ProvisionAsync(
            IReadOnlyList<CatalogBundle> bundles,
            IProgress<SchedulerProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (bundles == null) throw new ArgumentNullException(nameof(bundles));

            if (_options.EstimatedTotalBytes > 0 && !string.IsNullOrEmpty(_options.CacheDirectory))
                EnsureDiskCapacity(_options.EstimatedTotalBytes);

            int total = bundles.Count;
            var paths = new string[total];
            int completed = 0, failed = 0;
            long bytes = 0;
            var clock = Stopwatch.StartNew();

            int degree = _options.Concurrency == SchedulerConcurrency.Sequential ? 1 : Math.Max(1, _options.MaxConcurrency);
            using (var gate = new SemaphoreSlim(degree))
            {
                var tasks = new Task[total];
                for (int i = 0; i < total; i++)
                {
                    int idx = i;
                    CatalogBundle bundle = bundles[idx];
                    tasks[idx] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken);
                        try
                        {
                            string path = await ProvisionWithRetryAsync(bundle, cancellationToken);
                            paths[idx] = path;
                            Interlocked.Add(ref bytes, SafeLength(path));
                            int c = Interlocked.Increment(ref completed);
                            Report(progress, c, Volatile.Read(ref failed), total, Interlocked.Read(ref bytes), clock, _options.SpeedMeter);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            int f = Interlocked.Increment(ref failed);
                            Report(progress, Volatile.Read(ref completed), f, total, Interlocked.Read(ref bytes), clock, _options.SpeedMeter);
                        }
                        finally { gate.Release(); }
                    }, cancellationToken);
                }
                await Task.WhenAll(tasks);
            }

            return new SchedulerResult
            {
                Succeeded = completed,
                Failed = failed,
                BytesProvisioned = bytes,
                Paths = paths,
            };
        }

        // A failure is one of three kinds, each retried under its own budget/backoff (or not at all).
        private enum FailureKind { Fatal, Transient, Integrity }

        private async Task<string> ProvisionWithRetryAsync(CatalogBundle bundle, CancellationToken cancellationToken)
        {
            int transientBudget = Math.Max(1, _options.MaxItemRetries);
            int integrityBudget = Math.Max(1, _options.MaxIntegrityRetries);
            int transientUsed = 0, integrityUsed = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await ProvisionOnceAsync(bundle, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    switch (Classify(e))
                    {
                        case FailureKind.Fatal:
                            throw; // disk full / non-retryable — burning attempts cannot help
                        case FailureKind.Integrity:
                            if (++integrityUsed >= integrityBudget) throw;
                            if (_options.IntegrityRetryBackoff > TimeSpan.Zero)
                                await Task.Delay(_options.IntegrityRetryBackoff, cancellationToken);
                            break;
                        default: // Transient
                            if (++transientUsed >= transientBudget) throw;
                            // exponential backoff: base, 2×, 4×, …
                            long ticks = _options.RetryBackoff.Ticks * (1L << Math.Min(transientUsed - 1, 30));
                            if (ticks > 0) await Task.Delay(new TimeSpan(ticks), cancellationToken);
                            break;
                    }
                }
            }
        }

        // Wraps one provision attempt in the stall watchdog: if it makes no headway within StallTimeout it is
        // cancelled and surfaced as a transient failure so the retry loop can try afresh.
        private async Task<string> ProvisionOnceAsync(CatalogBundle bundle, CancellationToken cancellationToken)
        {
            if (_options.StallTimeout <= TimeSpan.Zero)
                return await _provisioner.EnsureBundleAsync(bundle, cancellationToken);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<string> attempt = _provisioner.EnsureBundleAsync(bundle, attemptCts.Token);
            Task timeout = Task.Delay(_options.StallTimeout, attemptCts.Token);
            Task first = await Task.WhenAny(attempt, timeout);
            if (first == attempt) return await attempt;

            attemptCts.Cancel(); // stalled — abandon the attempt
            throw new NetworkException("Bundle '" + bundle.Name + "' stalled (no completion within " + _options.StallTimeout + ").");
        }

        // Corrupt bytes (hash mismatch / decompress failure) are integrity failures; out-of-disk is fatal;
        // everything else (timeout, reset, 5xx, stall) is transient. RetryCountExceededException is unwrapped to
        // its underlying cause so a provisioner that already gave up on a corrupt download is still classified right.
        private static FailureKind Classify(Exception e)
        {
            switch (Unwrap(e))
            {
                case NotEnoughDiskCapacityException _: return FailureKind.Fatal;
                case HashMismatchException _:          return FailureKind.Integrity;
                case DecompressionFailedException _:   return FailureKind.Integrity;
                default:                               return FailureKind.Transient;
            }
        }

        private static Exception Unwrap(Exception e) =>
            e is RetryCountExceededException && e.InnerException != null ? e.InnerException : e;

        private void EnsureDiskCapacity(long needed)
        {
            long free;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_options.CacheDirectory));
                free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return; // can't determine free space → don't block the download on a guess
            }
            if (free < needed)
                throw new NotEnoughDiskCapacityException(
                    "Need ~" + needed + " bytes to provision content but only " + free + " bytes are free.");
        }

        private static void Report(IProgress<SchedulerProgress> progress, int completed, int failed, int total, long bytes, Stopwatch clock, DownloadSpeedMeter speedMeter)
        {
            double seconds = clock.Elapsed.TotalSeconds;
            double bytesPerSecond = seconds > 0 ? bytes / seconds : 0;

            // Classify the connection off the same aggregate rate (Good/Bad transitions raise events on the meter).
            speedMeter?.Sample(bytesPerSecond);

            if (progress == null) return;
            progress.Report(new SchedulerProgress
            {
                Completed = completed,
                Failed = failed,
                Total = total,
                BytesProvisioned = bytes,
                BytesPerSecond = bytesPerSecond,
            });
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
    }
}
