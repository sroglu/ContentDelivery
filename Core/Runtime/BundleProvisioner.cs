using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Turns a catalog bundle into a verified, ready-to-load local file. Content-addressed: the bundle's
    /// <see cref="CatalogBundle.Hash"/> is the remote object name (download + verify target), and the cache file is
    /// named by <see cref="CatalogBundle.UncompressedHash"/> — so the cache holds the RAW bundle exactly once (1×),
    /// even for compressed content: the compressed blob is verified and decompressed in-flight, never persisted.
    /// A present cache file is trusted by its content-addressed name (no per-launch re-hash of a large bundle).
    /// Download is the one external boundary, so retry/backoff + hash verification concentrate here; transports stay dumb.
    /// </summary>
    public sealed class BundleProvisioner
    {
        private readonly IDownloadTransport _transport;
        private readonly IContentHasher _hasher;
        private readonly string _cacheDirectory;
        private readonly string _baseUrl;
        private readonly string _localBaseUrl;
        private readonly int _maxAttempts;
        // Bounds how many bundles decompress at once (CPU-bound + holds a working buffer each) — a count-based
        // mem/CPU guard; leaves a core for the main thread.
        private readonly SemaphoreSlim _decompressGate = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));

        /// <param name="baseUrl">Origin for Remote (CDN) bundles.</param>
        /// <param name="localBaseUrl">
        /// Origin for <c>Local</c> (build-shipped) bundles — the StreamingAssets content directory as a fetchable
        /// URL. Null falls back to <paramref name="baseUrl"/>, so a remote-only catalog needs only the one origin.
        /// </param>
        /// <param name="hasher">Content hasher; null = SHA-256. MUST match the hasher the catalog was built with.</param>
        public BundleProvisioner(
            IDownloadTransport transport, string cacheDirectory, string baseUrl, string localBaseUrl = null,
            int maxAttempts = 3, IContentHasher hasher = null)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            _transport = transport;
            _hasher = hasher ?? new Sha256ContentHasher();
            _cacheDirectory = cacheDirectory;
            _baseUrl = baseUrl;
            _localBaseUrl = localBaseUrl;
            _maxAttempts = maxAttempts;
        }

        /// <summary>The cache path of the ready-to-load (raw) bundle, named by its uncompressed content hash.</summary>
        public string GetCachePath(CatalogBundle bundle) =>
            Path.Combine(_cacheDirectory, string.IsNullOrEmpty(bundle.UncompressedHash) ? bundle.Hash : bundle.UncompressedHash);

        /// <summary>
        /// Ensures a verified, ready-to-load local copy of <paramref name="bundle"/> and returns its path. A present
        /// cache file (named by the uncompressed hash) is trusted without re-hashing. On a miss, downloads the stored
        /// object with retry, verifies it against <see cref="CatalogBundle.Hash"/>, then materializes the RAW bundle:
        /// uncompressed content is written as-is; compressed content is stream-decompressed in-flight (the compressed
        /// blob is never persisted → the cache stays 1×).
        /// </summary>
        public async Task<string> EnsureBundleAsync(CatalogBundle bundle, CancellationToken cancellationToken = default)
        {
            string loadable = GetCachePath(bundle);
            // Atomic writes mean a present file is complete; trust the content-addressed name (no per-launch re-hash).
            if (File.Exists(loadable)) return loadable;

            // Local content ships in the build (StreamingAssets origin); remote content comes from the CDN.
            string origin = bundle.Local && !string.IsNullOrEmpty(_localBaseUrl) ? _localBaseUrl : _baseUrl;
            string url = CombineUrl(origin, bundle.Hash);
            Exception last = null;
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    byte[] stored = await _transport.DownloadBytesAsync(url, cancellationToken);
                    if (!_hasher.Verify(stored, bundle.Hash))
                        throw new HashMismatchException(bundle.Name);
                    await MaterializeAsync(bundle, stored, loadable, cancellationToken);
                    return loadable;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ContentDeliveryException e) when (!e.Retryable)
                {
                    throw; // fatal (no disk, corrupt archive) — don't waste the remaining attempts
                }
                catch (Exception e)
                {
                    last = e;
                }
            }
            throw new RetryCountExceededException(bundle.Name, _maxAttempts, last);
        }

        /// <summary>
        /// Writes the ready-to-load (raw) bundle to <paramref name="loadablePath"/> from the verified stored bytes.
        /// Uncompressed content is the bundle itself. Compressed content is decompressed by streaming the in-memory
        /// blob straight into the cache file (input + output streamed → low peak memory) — the compressed blob is
        /// never written to disk, so the cache is 1×. Decompression runs off-thread under a concurrency gate, after
        /// a disk-capacity precheck against the uncompressed size the LZMA header declares.
        /// </summary>
        private async Task MaterializeAsync(CatalogBundle bundle, byte[] stored, string loadablePath, CancellationToken cancellationToken)
        {
            if (bundle.Compression == BundleCompression.None)
            {
                WriteCacheAtomic(loadablePath, stored);
                return;
            }

            long uncompressedLength = PFound.Lzma.Lzma.ReadUncompressedLength(stored);
            EnsureDiskCapacity(uncompressedLength, bundle.Name);

            await _decompressGate.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    string temp = loadablePath + ".tmp";
                    using (var src = new MemoryStream(stored, false))
                    using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
                        PFound.Lzma.Lzma.DecompressInto(src, dst);
                    if (File.Exists(loadablePath)) File.Delete(loadablePath);
                    File.Move(temp, loadablePath);
                }, cancellationToken);
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is NotEnoughDiskCapacityException))
            {
                throw new DecompressionFailedException("Failed to decompress bundle '" + bundle.Name + "'.", e);
            }
            finally
            {
                _decompressGate.Release();
            }
        }

        // Refuse to decompress when the cache drive can't hold the result (re-adds old AssetSystem robustness).
        private void EnsureDiskCapacity(long needed, string bundleName)
        {
            long free;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_cacheDirectory));
                free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return; // can't determine free space → don't block on a guess
            }
            if (free < needed)
                throw new NotEnoughDiskCapacityException(
                    "Cannot cache bundle '" + bundleName + "': need " + needed + " bytes, " + free + " free.");
        }

        /// <summary>Ensures a whole dependency closure in dependencies-first order; returns each local path.</summary>
        public async Task<IReadOnlyList<string>> EnsureClosureAsync(
            IReadOnlyList<CatalogBundle> closure, CancellationToken cancellationToken = default)
        {
            var paths = new List<string>(closure.Count);
            for (int i = 0; i < closure.Count; i++)
                paths.Add(await EnsureBundleAsync(closure[i], cancellationToken));
            return paths;
        }

        private void WriteCacheAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(_cacheDirectory);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string CombineUrl(string baseUrl, string name)
        {
            if (string.IsNullOrEmpty(baseUrl)) return name;
            return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl + name : baseUrl + "/" + name;
        }
    }
}
