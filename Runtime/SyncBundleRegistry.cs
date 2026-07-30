using System;
using System.Collections.Generic;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// The blocking counterpart of <c>LoadedBundleRegistry</c>: loads a bundle and its dependency closure from
    /// disk synchronously (<see cref="AssetBundle.LoadFromFile(string)"/>) and ref-counts each by content hash, so
    /// a bundle shared across addresses loads once and unloads only when its last holder releases it. The loadable
    /// file for a bundle is located by an injected resolver (cache path for remote, embedded path for local) so the
    /// registry stays free of platform-path policy. Main-thread only.
    /// </summary>
    public sealed class SyncBundleRegistry
    {
        private sealed class Loaded { public AssetBundle Bundle; public int RefCount; }

        private readonly Dictionary<string, Loaded> _loaded = new Dictionary<string, Loaded>(StringComparer.Ordinal);
        private readonly Func<CatalogBundle, string> _resolveLoadablePath;

        /// <param name="resolveLoadablePath">Maps a bundle to the on-disk file to <c>LoadFromFile</c>.</param>
        public SyncBundleRegistry(Func<CatalogBundle, string> resolveLoadablePath)
        {
            if (resolveLoadablePath == null) throw new ArgumentNullException(nameof(resolveLoadablePath));
            _resolveLoadablePath = resolveLoadablePath;
        }

        /// <summary>
        /// Loads every bundle in <paramref name="closure"/> (dependencies first), taking <paramref name="count"/>
        /// references on each, and returns the primary bundle (the closure's last entry, which owns the asset).
        /// </summary>
        public AssetBundle AcquireClosure(IReadOnlyList<CatalogBundle> closure, int count)
        {
            AssetBundle primary = null;
            for (int i = 0; i < closure.Count; i++)
                primary = Acquire(closure[i], count);
            return primary;
        }

        private AssetBundle Acquire(CatalogBundle bundle, int count)
        {
            if (_loaded.TryGetValue(bundle.Hash, out var entry))
            {
                entry.RefCount += count;
                return entry.Bundle;
            }

            string path = _resolveLoadablePath(bundle);
            AssetBundle ab = AssetBundle.LoadFromFile(path);
            _loaded[bundle.Hash] = new Loaded { Bundle = ab, RefCount = count };
            return ab;
        }

        /// <summary>Releases <paramref name="count"/> references on each bundle in <paramref name="closure"/>; unloads at zero.</summary>
        public void ReleaseClosure(IReadOnlyList<CatalogBundle> closure, int count)
        {
            for (int i = 0; i < closure.Count; i++)
            {
                var bundle = closure[i];
                if (!_loaded.TryGetValue(bundle.Hash, out var entry)) continue;
                entry.RefCount -= count;
                if (entry.RefCount > 0) continue;

                entry.Bundle.Unload(false);
                _loaded.Remove(bundle.Hash);
            }
        }

        /// <summary>Whether a bundle is currently resident (diagnostics/tests).</summary>
        public bool IsLoaded(string bundleHash) => _loaded.ContainsKey(bundleHash);

        /// <summary>Unloads every resident bundle (a full catalog swap / teardown).</summary>
        public void UnloadAll()
        {
            foreach (var kv in _loaded) kv.Value.Bundle.Unload(false);
            _loaded.Clear();
        }
    }
}
