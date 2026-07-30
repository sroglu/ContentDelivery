using System;
using System.Collections.Generic;
using UnityEditor;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>One asset pulled implicitly into more than one bundle — duplicated bytes in each (wasted download/disk).</summary>
    public sealed class DuplicateDependency
    {
        public string Asset;       // the shared (implicit) dependency asset path
        public string[] Bundles;   // the bundles that each embed a copy of it
    }

    /// <summary>
    /// Pre-build duplicate/waste analysis: an asset that is NOT explicitly assigned to any bundle but is a
    /// dependency of explicit assets in two or more bundles gets a copy serialized into each of those bundles.
    /// This finds those before a build — the same insight as the Addressables "duplicate bundle dependencies"
    /// rule — using only <see cref="AssetDatabase.GetDependencies(string,bool)"/>, so it runs without a build and
    /// without SBP internals. (Explicit assets are placed once and referenced, so they are never the waste.)
    /// </summary>
    public static class BundleDuplicateAnalyzer
    {
        /// <summary>
        /// Pure core: given each bundle's set of implicit dependency assets, return the dependencies that appear in
        /// two or more bundles (sorted by asset path; each result's bundles sorted). SBP-free and engine-light, so
        /// it is unit-testable on its own.
        /// </summary>
        public static List<DuplicateDependency> FindDuplicates(
            IReadOnlyDictionary<string, ICollection<string>> bundleToImplicitDeps)
        {
            if (bundleToImplicitDeps == null) throw new ArgumentNullException(nameof(bundleToImplicitDeps));

            // Invert to dependency → the bundles that pull it in.
            var assetToBundles = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var kv in bundleToImplicitDeps)
            {
                string bundle = kv.Key;
                if (kv.Value == null) continue;
                foreach (var asset in kv.Value)
                {
                    if (!assetToBundles.TryGetValue(asset, out var bundles))
                        assetToBundles[asset] = bundles = new SortedSet<string>(StringComparer.Ordinal);
                    bundles.Add(bundle);
                }
            }

            var duplicates = new List<DuplicateDependency>();
            foreach (var kv in assetToBundles)
                if (kv.Value.Count >= 2)
                {
                    var bundles = new string[kv.Value.Count];
                    kv.Value.CopyTo(bundles);
                    duplicates.Add(new DuplicateDependency { Asset = kv.Key, Bundles = bundles });
                }
            duplicates.Sort((x, y) => string.CompareOrdinal(x.Asset, y.Asset));
            return duplicates;
        }

        /// <summary>
        /// Runs the analysis over the given groups: assigns each explicit asset to its bundle (same rules as the
        /// build), walks each bundle's recursive dependencies, drops the assets that are themselves explicit
        /// (placed, not duplicated), then reports the implicit dependencies shared across bundles.
        /// </summary>
        public static List<DuplicateDependency> Analyze(IReadOnlyList<AssetGroup> groups)
        {
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            // 1. Every explicit asset path → its owning bundle, and the set of all explicit assets.
            var bundleToExplicit = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var explicitAssets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                if (group == null || group.Entries == null) continue;
                string groupBundle = group.ResolveBundleName();
                bool separate = group.Packing == BundlePackingMode.PackSeparately;
                foreach (var e in group.Entries)
                {
                    if (e == null || e.Asset == null) continue;
                    string path = AssetDatabase.GetAssetPath(e.Asset);
                    if (string.IsNullOrEmpty(path)) continue;
                    string bundle = separate ? groupBundle + "_" + e.Address : groupBundle;
                    if (!bundleToExplicit.TryGetValue(bundle, out var list))
                        bundleToExplicit[bundle] = list = new List<string>();
                    list.Add(path);
                    explicitAssets.Add(path);
                }
            }

            // 2. Per bundle, the implicit dependency assets = recursive deps of its explicit assets, minus any asset
            //    that is itself explicit anywhere (those are placed once and referenced, never duplicated).
            var bundleToImplicit = new Dictionary<string, ICollection<string>>(StringComparer.Ordinal);
            foreach (var kv in bundleToExplicit)
            {
                var implicitDeps = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dep in AssetDatabase.GetDependencies(kv.Value.ToArray(), true))
                {
                    if (explicitAssets.Contains(dep)) continue;       // placed once + referenced, never duplicated
                    if (dep.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue; // scripts aren't bundled data
                    implicitDeps.Add(dep);
                }
                bundleToImplicit[kv.Key] = implicitDeps;
            }

            return FindDuplicates(bundleToImplicit);
        }
    }
}
