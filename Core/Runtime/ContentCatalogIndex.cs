using System;
using System.Collections.Generic;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// One address resolved to everything a loader needs: the owning bundle, the asset name to pull from that
    /// bundle, and — for a sprite packed in an atlas / a sub-asset — the sub-object name to extract after the main
    /// asset loads. Built once from a <see cref="CatalogAsset"/>; the atlas name is the cached
    /// <see cref="SubAssetName"/>, so the sprite lookup is a dictionary hit, never a per-load string parse.
    /// </summary>
    public readonly struct ResolvedAsset
    {
        public readonly string Address;
        public readonly string Bundle;
        /// <summary>Name of the asset to load from the bundle (the "main" object for a sub-asset address).</summary>
        public readonly string MainAssetName;
        /// <summary>Sub-object to extract after the main asset loads (sprite-in-atlas); null for a direct load.</summary>
        public readonly string SubAssetName;
        public readonly int Phase;

        public ResolvedAsset(string address, string bundle, string mainAssetName, string subAssetName, int phase)
        {
            Address = address;
            Bundle = bundle;
            MainAssetName = mainAssetName;
            SubAssetName = subAssetName;
            Phase = phase;
        }

        /// <summary>True when the address targets a sub-object (sprite atlas / mesh-in-fbx) rather than a whole asset.</summary>
        public bool HasSubAsset => SubAssetName != null;
    }

    /// <summary>
    /// The precomputed lookup tables an app-owned catalog needs for fast, allocation-free resolution: address →
    /// resolved asset (bundle + in-bundle name + cached atlas sprite name), and bundle name → bundle info. Built
    /// once by <c>Initialize</c> from the deserialized <see cref="Catalog"/>; a new catalog builds a fresh index
    /// (no merge). Pure Core — the sub-asset split and every table are engine-free and unit-testable, so only the
    /// actual bundle loading and refcounting (which drive "unload at refcount 0") live in the Unity registry.
    /// </summary>
    public sealed class ContentCatalogIndex
    {
        private readonly Catalog _catalog;
        private readonly Dictionary<string, ResolvedAsset> _byAddress;

        public ContentCatalogIndex(Catalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            _catalog = catalog;

            _byAddress = new Dictionary<string, ResolvedAsset>(StringComparer.Ordinal);
            foreach (var a in catalog.AllAssets)
            {
                TrySplitSubAsset(a.AssetName, out string main, out string sub);
                _byAddress[a.Address] = new ResolvedAsset(a.Address, a.Bundle, main, sub, a.Phase);
            }
        }

        /// <summary>The catalog this index was built over (bundle graph, packs, version).</summary>
        public Catalog Catalog => _catalog;

        /// <summary>Resolves an address to its bundle + in-bundle name (+ atlas sub-name); false when unknown.</summary>
        public bool TryResolve(string address, out ResolvedAsset resolved) => _byAddress.TryGetValue(address, out resolved);

        /// <summary>Bundle info by name (hash, dependencies, Local flag, compression).</summary>
        public bool TryGetBundle(string bundleName, out CatalogBundle bundle) => _catalog.TryGetBundle(bundleName, out bundle);

        /// <summary>Number of addresses in the catalog (diagnostics).</summary>
        public int AddressCount => _byAddress.Count;

        /// <summary>
        /// Splits an in-bundle name of the form <c>main[sub]</c> into its main asset and sub-object (the sprite-atlas
        /// convention the build pipeline emits); a plain name yields itself as <paramref name="main"/> and a null
        /// <paramref name="sub"/>. A trailing ']' with a non-empty selector after a '[' is required.
        /// </summary>
        public static bool TrySplitSubAsset(string assetName, out string main, out string sub)
        {
            main = assetName;
            sub = null;
            if (string.IsNullOrEmpty(assetName) || assetName[assetName.Length - 1] != ']') return false;
            int open = assetName.IndexOf('[');
            if (open <= 0) return false;
            string candidate = assetName.Substring(open + 1, assetName.Length - open - 2);
            if (candidate.Length == 0) return false;
            main = assetName.Substring(0, open);
            sub = candidate;
            return true;
        }
    }
}
