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
        public int MaxItemRetries = 5;                 // whole-bundle retries on top of the provisioner's own attempts
        public TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(200); // base; doubled each retry (exponential)
        public long EstimatedTotalBytes = 0;           // if > 0, a disk-capacity precheck runs before downloading
        public string CacheDirectory;                  // drive checked for free space (required for the disk precheck)
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
                            Report(progress, c, Volatile.Read(ref failed), total, Interlocked.Read(ref bytes), clock);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            int f = Interlocked.Increment(ref failed);
                            Report(progress, Volatile.Read(ref completed), f, total, Interlocked.Read(ref bytes), clock);
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

        private async Task<string> ProvisionWithRetryAsync(CatalogBundle bundle, CancellationToken cancellationToken)
        {
            int attempts = Math.Max(1, _options.MaxItemRetries);
            Exception last = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await _provisioner.EnsureBundleAsync(bundle, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (NotEnoughDiskCapacityException) { throw; }    // fatal — retrying cannot help
                catch (DecompressionFailedException) { throw; }      // fatal — the bytes are corrupt
                catch (Exception e)
                {
                    last = e;
                    if (attempt < attempts)
                    {
                        // exponential backoff: base, 2×, 4×, …
                        long ticks = _options.RetryBackoff.Ticks * (1L << (attempt - 1));
                        if (ticks > 0) await Task.Delay(new TimeSpan(ticks), cancellationToken);
                    }
                }
            }
            throw last;
        }

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

        private static void Report(IProgress<SchedulerProgress> progress, int completed, int failed, int total, long bytes, Stopwatch clock)
        {
            if (progress == null) return;
            double seconds = clock.Elapsed.TotalSeconds;
            progress.Report(new SchedulerProgress
            {
                Completed = completed,
                Failed = failed,
                Total = total,
                BytesProvisioned = bytes,
                BytesPerSecond = seconds > 0 ? bytes / seconds : 0,
            });
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
    }
}
