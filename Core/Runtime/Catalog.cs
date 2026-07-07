using System;
using System.Collections.Generic;

namespace PFound.ContentDelivery.Core
{
    /// <summary>How a bundle's bytes are compressed for transfer/storage (above Unity's own bundle compression).</summary>
    public enum BundleCompression
    {
        None = 0,   // the stored object is the bundle as-is (Unity's internal compression only)
        Lzma = 1,   // the stored object is LZMA of an uncompressed bundle; decompress before loading
    }

    /// <summary>A built bundle: a content-addressed name (its hash) and the bundles it depends on.</summary>
    public sealed class CatalogBundle
    {
        public string Name;                              // logical bundle id
        public string Hash;                              // content hash of the STORED object (compressed, if any)
        public string UncompressedHash;                  // content hash of the raw bundle (== Hash when uncompressed);
                                                         // the loadable cache file is named by this, so cache stays 1×
        public string[] Dependencies = Array.Empty<string>(); // names of bundles that must load first
        public bool Local;                               // ships in the build (StreamingAssets) vs pulled from the CDN
        public BundleCompression Compression;            // transfer compression applied to the stored object
    }

    /// <summary>
    /// A named set of bundles that ship/download as one unit (an updatable content pack). Members are bundle
    /// <see cref="CatalogBundle.Name"/>s; their transitive dependencies are pulled in by the closure, so a pack
    /// need only list the bundles it directly owns.
    /// </summary>
    public sealed class CatalogPack
    {
        public string Name;                                // logical pack id
        public string[] Bundles = Array.Empty<string>();   // member bundle names (deps resolved via closure)
    }

    /// <summary>One addressable asset: which bundle holds it, the in-bundle asset name, and its load phase.</summary>
    public sealed class CatalogAsset
    {
        public string Address;     // stable string address callers resolve
        public string Bundle;      // owning bundle Name
        public string AssetName;   // asset name inside the bundle (sub-asset / alias support)
        public int Phase;          // AssetPhase priority
        public string[] Labels = Array.Empty<string>(); // free-form tags for group queries (load-all-by-label)
    }

    /// <summary>
    /// In-memory content catalog: address → asset → bundle, plus the bundle dependency graph.
    /// Pure data + resolution; the JSON (de)serialization lives in the IO/Unity layer. Building one
    /// in-memory is what makes resolution unit-testable without Unity.
    /// </summary>
    public sealed class Catalog
    {
        private readonly Dictionary<string, CatalogAsset> _assets;
        private readonly Dictionary<string, CatalogBundle> _bundles;
        private readonly Dictionary<string, CatalogPack> _packs;

        /// <summary>
        /// Opaque content version of this catalog. The runtime compares a freshly-fetched catalog's version to
        /// the cached one; if it changed, content is re-pulled (a single whole-catalog swap — no shard/diff).
        /// Null/empty when unversioned.
        /// </summary>
        public string Version { get; }

        public Catalog(
            IEnumerable<CatalogBundle> bundles, IEnumerable<CatalogAsset> assets,
            string version = null, IEnumerable<CatalogPack> packs = null)
        {
            if (bundles == null) throw new ArgumentNullException(nameof(bundles));
            if (assets == null) throw new ArgumentNullException(nameof(assets));
            Version = version;
            _bundles = new Dictionary<string, CatalogBundle>(StringComparer.Ordinal);
            foreach (var b in bundles) _bundles[b.Name] = b;
            _assets = new Dictionary<string, CatalogAsset>(StringComparer.Ordinal);
            foreach (var a in assets) _assets[a.Address] = a;
            _packs = new Dictionary<string, CatalogPack>(StringComparer.Ordinal);
            if (packs != null) foreach (var p in packs) _packs[p.Name] = p; // packs are optional
        }

        public bool TryResolveAsset(string address, out CatalogAsset asset) => _assets.TryGetValue(address, out asset);
        public bool TryGetBundle(string name, out CatalogBundle bundle) => _bundles.TryGetValue(name, out bundle);
        public bool TryGetPack(string name, out CatalogPack pack) => _packs.TryGetValue(name, out pack);

        /// <summary>All bundles / assets / packs (unordered) — for serialization and tooling.</summary>
        public IReadOnlyCollection<CatalogBundle> AllBundles => _bundles.Values;
        public IReadOnlyCollection<CatalogAsset> AllAssets => _assets.Values;
        public IReadOnlyCollection<CatalogPack> AllPacks => _packs.Values;

        /// <summary>
        /// Every catalog asset whose <see cref="CatalogAsset.Phase"/> is at or below <paramref name="maxPhaseInclusive"/>,
        /// i.e. the content a phased preload should bring up by that phase (Essential ⊂ Early ⊂ … ⊂ Deferred).
        /// Enumerated, not ordered — the caller dedups bundles and provisions dependencies-first per closure.
        /// </summary>
        public IEnumerable<CatalogAsset> AssetsUpToPhase(int maxPhaseInclusive)
        {
            foreach (var asset in _assets.Values)
                if (asset.Phase <= maxPhaseInclusive) yield return asset;
        }

        /// <summary>
        /// The distinct asset phase values at or below <paramref name="maxPhaseInclusive"/>, ascending — the order a
        /// phase-by-phase sequential preload walks (each phase gated on the previous).
        /// </summary>
        public IReadOnlyList<int> PhasesUpTo(int maxPhaseInclusive)
        {
            var distinct = new SortedSet<int>();
            foreach (var asset in _assets.Values)
                if (asset.Phase <= maxPhaseInclusive) distinct.Add(asset.Phase);
            return new List<int>(distinct);
        }

        /// <summary>Every asset at exactly <paramref name="phase"/> — the slice a sequential preload provisions per step.</summary>
        public IEnumerable<CatalogAsset> AssetsInPhase(int phase)
        {
            foreach (var asset in _assets.Values)
                if (asset.Phase == phase) yield return asset;
        }

        /// <summary>
        /// Every catalog asset tagged with <paramref name="label"/> (case-sensitive, ordinal) — the content a
        /// "load everything labelled X" query brings up. Enumerated, not ordered; the caller dedups bundles and
        /// provisions dependencies-first per closure, exactly as for <see cref="AssetsUpToPhase"/>.
        /// </summary>
        public IEnumerable<CatalogAsset> AssetsWithLabel(string label)
        {
            if (label == null) throw new ArgumentNullException(nameof(label));
            foreach (var asset in _assets.Values)
            {
                var labels = asset.Labels;
                for (int i = 0; i < labels.Length; i++)
                    if (string.Equals(labels[i], label, StringComparison.Ordinal)) { yield return asset; break; }
            }
        }

        /// <summary>
        /// The bundle plus all its transitive dependencies, in dependencies-first order (each bundle
        /// appears after the bundles it needs), deduplicated. Throws on a missing or cyclic dependency.
        /// </summary>
        public IReadOnlyList<CatalogBundle> GetBundleClosure(string bundleName)
        {
            var ordered = new List<CatalogBundle>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            Visit(bundleName, ordered, done, onStack);
            return ordered;
        }

        /// <summary>
        /// Every bundle a <paramref name="packName"/> needs provisioned — the union of each member bundle's
        /// closure, in dependencies-first order and deduplicated across members (a dependency shared by two
        /// members appears once). Throws on an unknown pack, or a missing/cyclic bundle dependency.
        /// </summary>
        public IReadOnlyList<CatalogBundle> GetPackClosure(string packName)
        {
            if (!_packs.TryGetValue(packName, out var pack))
                throw new InvalidOperationException("Unknown pack: " + packName + ".");

            var ordered = new List<CatalogBundle>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var members = pack.Bundles;
            for (int i = 0; i < members.Length; i++) Visit(members[i], ordered, done, onStack);
            return ordered;
        }

        private void Visit(string name, List<CatalogBundle> ordered, HashSet<string> done, HashSet<string> onStack)
        {
            if (done.Contains(name)) return;
            if (!onStack.Add(name)) throw new InvalidOperationException("Cyclic bundle dependency at " + name + ".");
            if (!_bundles.TryGetValue(name, out var bundle)) throw new InvalidOperationException("Unknown bundle: " + name + ".");

            var deps = bundle.Dependencies;
            for (int i = 0; i < deps.Length; i++) Visit(deps[i], ordered, done, onStack);

            onStack.Remove(name);
            done.Add(name);
            ordered.Add(bundle);
        }
    }
}
