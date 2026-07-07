using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ContentDelivery.Core
{
    /// <summary>Outcome of a <see cref="CatalogContentDownloader"/> run.</summary>
    public readonly struct CatalogContentResult
    {
        /// <summary>Bundles the run decided to fetch (absent + remote); 0 means the queue was empty.</summary>
        public readonly int Requested;
        public readonly int Succeeded;
        public readonly int Failed;
        public readonly long BytesProvisioned;
        /// <summary>True when nothing needed downloading (everything already resident) — the fast exit.</summary>
        public readonly bool Skipped;

        public CatalogContentResult(int requested, int succeeded, int failed, long bytes, bool skipped)
        {
            Requested = requested;
            Succeeded = succeeded;
            Failed = failed;
            BytesProvisioned = bytes;
            Skipped = skipped;
        }
    }

    /// <summary>
    /// Bulk, conditional content provisioning for a whole catalog: works out which bundles are actually missing,
    /// batches them pack-by-pack (then the standalone remainder), and provisions the deduplicated set concurrently
    /// with retry, decompression and per-bundle hash validation via the existing <see cref="DownloadScheduler"/> +
    /// <see cref="BundleProvisioner"/>. An empty queue short-circuits (<see cref="CatalogContentResult.Skipped"/>).
    ///
    /// <para>Pack model: PFound stores every bundle as its own content-addressed object (the hash IS the file name),
    /// so a "pack" is purely a logical grouping, not a packed blob. "Download the whole pack once" therefore means
    /// enqueue every not-yet-present bundle in the pack's closure as a single batch; each bundle is still fetched and
    /// hash-validated on its own, so packed and standalone bundles are indistinguishable to the loader.</para>
    ///
    /// Pure Core (BCL + the engine-free provisioner/scheduler) — schedulable and testable without Unity.
    /// </summary>
    public sealed class CatalogContentDownloader
    {
        private readonly BundleProvisioner _provisioner;
        private readonly DownloadSchedulerOptions _schedulerOptions;

        /// <param name="transport">HTTP fetch boundary.</param>
        /// <param name="cacheDirectory">Disk cache for provisioned (decompressed, loadable) bundles.</param>
        /// <param name="remoteConfig">Origin/platform/catalog coordinates; supplies the bundle download URL.</param>
        /// <param name="hasher">Content hasher; MUST match the one the catalog was built with.</param>
        /// <param name="schedulerOptions">Concurrency/retry tuning; null = the scheduler defaults.</param>
        public CatalogContentDownloader(
            IDownloadTransport transport, string cacheDirectory, RemoteContentConfig remoteConfig,
            IContentHasher hasher, DownloadSchedulerOptions schedulerOptions = null)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (hasher == null) throw new ArgumentNullException(nameof(hasher));
            _provisioner = new BundleProvisioner(
                transport, cacheDirectory, remoteConfig.ResolveContentUrl(), localBaseUrl: null, hasher: hasher);
            _schedulerOptions = schedulerOptions ?? new DownloadSchedulerOptions { CacheDirectory = cacheDirectory };
        }

        /// <summary>
        /// Provisions every remote bundle the catalog needs that is not already cached, in pack-grouped then
        /// standalone order, deduplicated. <paramref name="progress"/> is pushed aggregate counts as bundles land.
        /// </summary>
        public async Task<CatalogContentResult> DownloadCatalogContentAsync(
            Catalog catalog,
            IProgress<SchedulerProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            var queue = BuildDownloadQueue(catalog);
            if (queue.Count == 0)
                return new CatalogContentResult(0, 0, 0, 0, skipped: true);

            var scheduler = new DownloadScheduler(_provisioner, _schedulerOptions);
            SchedulerResult run = await scheduler.ProvisionAsync(queue, progress, cancellationToken);

            return new CatalogContentResult(queue.Count, run.Succeeded, run.Failed, run.BytesProvisioned, skipped: false);
        }

        /// <summary>
        /// The deduplicated, pack-grouped list of bundles to fetch: for each pack whose closure has any missing
        /// member, all of that closure's missing remote bundles; then the missing remote bundles owned by no pack.
        /// Local (embedded) bundles and already-cached bundles are excluded. Pure — the network-free half.
        /// </summary>
        public List<CatalogBundle> BuildDownloadQueue(Catalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            var queue = new List<CatalogBundle>();
            var enqueued = new HashSet<string>(StringComparer.Ordinal);
            var inSomePack = new HashSet<string>(StringComparer.Ordinal);

            // Pack-grouped: a pack with any missing bundle contributes its whole missing closure as one batch.
            foreach (var pack in catalog.AllPacks)
            {
                var closure = catalog.GetPackClosure(pack.Name);
                for (int i = 0; i < closure.Count; i++) inSomePack.Add(closure[i].Name);

                bool anyMissing = false;
                for (int i = 0; i < closure.Count; i++)
                    if (NeedsDownload(closure[i])) { anyMissing = true; break; }
                if (!anyMissing) continue;

                for (int i = 0; i < closure.Count; i++)
                    TryEnqueue(closure[i], queue, enqueued);
            }

            // Standalone: bundles owned by no pack, plus dependency closure, fetched when missing.
            foreach (var bundle in catalog.AllBundles)
            {
                if (inSomePack.Contains(bundle.Name)) continue;
                if (!NeedsDownload(bundle)) continue;
                var closure = catalog.GetBundleClosure(bundle.Name);
                for (int i = 0; i < closure.Count; i++)
                    TryEnqueue(closure[i], queue, enqueued);
            }

            return queue;
        }

        // A bundle needs downloading only when it is remote (not build-embedded) and its loadable cache file is absent.
        private bool NeedsDownload(CatalogBundle bundle) =>
            !bundle.Local && !File.Exists(_provisioner.GetCachePath(bundle));

        private void TryEnqueue(CatalogBundle bundle, List<CatalogBundle> queue, HashSet<string> enqueued)
        {
            if (!NeedsDownload(bundle)) return;
            if (!enqueued.Add(bundle.Name)) return;
            queue.Add(bundle);
        }
    }
}
