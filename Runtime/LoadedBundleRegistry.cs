using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Ref-counted registry of in-memory <see cref="AssetBundle"/>s, keyed by content hash. A bundle
    /// (and each of its transitive dependencies) is loaded from disk on first acquire and unloaded only
    /// when the last holder releases it, so dependencies shared across addresses are loaded once. The
    /// <see cref="BundleProvisioner"/> guarantees a hash-verified local file before we ever touch
    /// <c>LoadFromFileAsync</c>. Main-thread only.
    /// </summary>
    internal sealed class LoadedBundleRegistry
    {
        private sealed class Loaded { public AssetBundle Bundle; public int RefCount; public string Name; public long SizeOnDisk; }

        // Provision wraps the engine-free Core download→verify→decompress (where Unity.Profiling can't reach);
        // the load marker wraps the actual AssetBundle.LoadFromFileAsync.
        private static readonly ProfilerMarker s_provisionMarker = new(ProfilerCategory.Loading, "ContentDelivery.Provision");
        private static readonly ProfilerMarker s_loadFileMarker = new(ProfilerCategory.Loading, "ContentDelivery.LoadBundleFile");

        private readonly BundleProvisioner _provisioner;
        private readonly Dictionary<string, Loaded> _loaded = new Dictionary<string, Loaded>();

        public LoadedBundleRegistry(BundleProvisioner provisioner) { _provisioner = provisioner; }

        /// <summary>
        /// Ensures every bundle in <paramref name="closure"/> (dependencies first) is provisioned and
        /// loaded, taking one reference on each, and returns the loaded primary bundle (the closure's
        /// last entry — the one that owns the requested asset).
        /// </summary>
        public async UniTask<AssetBundle> AcquireClosureAsync(IReadOnlyList<CatalogBundle> closure)
        {
            AssetBundle primary = null;
            for (int i = 0; i < closure.Count; i++)
                primary = await AcquireAsync(closure[i]);
            return primary;
        }

        private async UniTask<AssetBundle> AcquireAsync(CatalogBundle bundle)
        {
            if (_loaded.TryGetValue(bundle.Hash, out var entry))
            {
                entry.RefCount++;
                return entry.Bundle;
            }

            string path;
            using (s_provisionMarker.Auto())
                path = await _provisioner.EnsureBundleAsync(bundle);

            // On-disk size of the loadable file we are about to load (the cheap, always-available size figure).
            long sizeOnDisk = new FileInfo(path).Length;

            AssetBundle ab;
            using (s_loadFileMarker.Auto())
            {
                var request = AssetBundle.LoadFromFileAsync(path);
                await request.ToUniTask();
                ab = request.assetBundle;
            }

            // A concurrent acquire of the same hash may have populated the slot while we awaited; if so,
            // keep the first-loaded bundle and discard this duplicate to preserve a single instance.
            if (_loaded.TryGetValue(bundle.Hash, out entry))
            {
                if (ab != null && ab != entry.Bundle) ab.Unload(false);
                entry.RefCount++;
                return entry.Bundle;
            }

            _loaded[bundle.Hash] = new Loaded { Bundle = ab, RefCount = 1, Name = bundle.Name, SizeOnDisk = sizeOnDisk };
            return ab;
        }

        /// <summary>Releases one reference on each bundle in <paramref name="closure"/>; unloads at zero.</summary>
        public void ReleaseClosure(IReadOnlyList<CatalogBundle> closure)
        {
            for (int i = 0; i < closure.Count; i++)
            {
                var bundle = closure[i];
                if (!_loaded.TryGetValue(bundle.Hash, out var entry)) continue;
                if (--entry.RefCount > 0) continue;

                if (entry.Bundle != null) entry.Bundle.Unload(false);
                _loaded.Remove(bundle.Hash);
            }
        }

        /// <summary>Snapshots every resident bundle into detached <see cref="BundleMemoryRow"/>s for the report.</summary>
        public IReadOnlyList<BundleMemoryRow> SnapshotMemoryRows()
        {
            var rows = new List<BundleMemoryRow>(_loaded.Count);
            foreach (var kv in _loaded)
                rows.Add(new BundleMemoryRow(kv.Value.Name, kv.Key, kv.Value.RefCount, kv.Value.SizeOnDisk));
            return rows;
        }
    }
}
