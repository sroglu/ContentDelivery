using System.Collections.Generic;
using System.Text;

namespace PFound.ContentDelivery
{
    /// <summary>How many references a single owner (an <see cref="AssetLoaderId"/>) holds — one slot of an
    /// asset's per-owner breakdown, or a per-owner total rolled up across every loaded asset.</summary>
    public readonly struct LoaderRef
    {
        public readonly string Loader;
        public readonly int RefCount;
        public LoaderRef(string loader, int refCount) { Loader = loader; RefCount = refCount; }
    }

    /// <summary>One resident asset's contribution to the report: what it is, who holds it, and (optionally) its
    /// measured runtime footprint. Pure primitives — no engine handle — so a report is a detached snapshot.</summary>
    public readonly struct AssetMemoryRow
    {
        public readonly string Address;
        public readonly string TypeName;                 // UnityEngine type of the loaded object
        public readonly int RefCount;                    // total across all owners
        public readonly string Source;                   // resolving IAssetSource (type name)
        public readonly IReadOnlyList<LoaderRef> ByLoader; // per-owner breakdown
        public readonly long RuntimeMemorySize;          // bytes from a deep pass; -1 when not measured

        public AssetMemoryRow(string address, string typeName, int refCount, string source,
            IReadOnlyList<LoaderRef> byLoader, long runtimeMemorySize)
        {
            Address = address;
            TypeName = typeName;
            RefCount = refCount;
            Source = source;
            ByLoader = byLoader;
            RuntimeMemorySize = runtimeMemorySize;
        }
    }

    /// <summary>One resident bundle's contribution: its content hash, how many holders keep it loaded, and the
    /// on-disk size of the loadable cache file it was loaded from.</summary>
    public readonly struct BundleMemoryRow
    {
        public readonly string Name;        // logical bundle id
        public readonly string Hash;        // content hash (the registry key)
        public readonly int RefCount;       // live holders keeping it in memory
        public readonly long SizeOnDisk;    // loadable cache-file length in bytes; -1 if unknown

        public BundleMemoryRow(string name, string hash, int refCount, long sizeOnDisk)
        {
            Name = name;
            Hash = hash;
            RefCount = refCount;
            SizeOnDisk = sizeOnDisk;
        }
    }

    /// <summary>
    /// A point-in-time snapshot of what ContentDelivery is keeping resident: the loaded assets
    /// (<see cref="AssetManager"/>) and the loaded bundles (<see cref="LoadedBundleRegistry"/>), plus roll-ups.
    /// Built by <see cref="ContentMemoryReporter"/> from the live registries; the data model itself is pure C#
    /// (no engine types) so the roll-up logic is unit-testable without the Profiler or Play mode.
    /// </summary>
    public sealed class ContentMemoryReport
    {
        public IReadOnlyList<AssetMemoryRow> Assets { get; }
        public IReadOnlyList<BundleMemoryRow> Bundles { get; }

        public int TotalLoadedAssets { get; }
        public int TotalLoadedBundles { get; }
        public int TotalAssetRefCount { get; }
        public int TotalBundleRefCount { get; }
        public long TotalSizeOnDisk { get; }
        /// <summary>Sum of per-asset runtime memory when a deep pass measured it; -1 when no deep pass ran.</summary>
        public long TotalRuntimeMemorySize { get; }
        /// <summary>Total references each owner holds across every loaded asset, ascending by loader id.</summary>
        public IReadOnlyList<LoaderRef> ByLoaderTotals { get; }

        private ContentMemoryReport(
            IReadOnlyList<AssetMemoryRow> assets, IReadOnlyList<BundleMemoryRow> bundles,
            int totalAssetRefCount, int totalBundleRefCount, long totalSizeOnDisk, long totalRuntimeMemorySize,
            IReadOnlyList<LoaderRef> byLoaderTotals)
        {
            Assets = assets;
            Bundles = bundles;
            TotalLoadedAssets = assets.Count;
            TotalLoadedBundles = bundles.Count;
            TotalAssetRefCount = totalAssetRefCount;
            TotalBundleRefCount = totalBundleRefCount;
            TotalSizeOnDisk = totalSizeOnDisk;
            TotalRuntimeMemorySize = totalRuntimeMemorySize;
            ByLoaderTotals = byLoaderTotals;
        }

        /// <summary>Rolls asset and bundle rows up into totals (counts, ref-counts, disk + runtime size,
        /// per-owner totals). Pure: feed it fabricated rows to test the aggregation in isolation.</summary>
        public static ContentMemoryReport From(IReadOnlyList<AssetMemoryRow> assets, IReadOnlyList<BundleMemoryRow> bundles)
        {
            int assetRefs = 0;
            long runtimeTotal = 0;
            bool anyMeasured = false;
            var perLoader = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
            for (int i = 0; i < assets.Count; i++)
            {
                var a = assets[i];
                assetRefs += a.RefCount;
                if (a.RuntimeMemorySize >= 0) { runtimeTotal += a.RuntimeMemorySize; anyMeasured = true; }
                var by = a.ByLoader;
                for (int j = 0; j < by.Count; j++)
                {
                    perLoader.TryGetValue(by[j].Loader, out int n);
                    perLoader[by[j].Loader] = n + by[j].RefCount;
                }
            }

            int bundleRefs = 0;
            long diskTotal = 0;
            for (int i = 0; i < bundles.Count; i++)
            {
                bundleRefs += bundles[i].RefCount;
                if (bundles[i].SizeOnDisk >= 0) diskTotal += bundles[i].SizeOnDisk;
            }

            var byLoaderTotals = new List<LoaderRef>(perLoader.Count);
            foreach (var kv in perLoader) byLoaderTotals.Add(new LoaderRef(kv.Key, kv.Value));

            return new ContentMemoryReport(
                assets, bundles, assetRefs, bundleRefs, diskTotal,
                anyMeasured ? runtimeTotal : -1, byLoaderTotals);
        }

        /// <summary>A human-readable dump for the console / dev overlay.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("ContentDelivery memory: ")
              .Append(TotalLoadedAssets).Append(" assets (")
              .Append(TotalAssetRefCount).Append(" refs), ")
              .Append(TotalLoadedBundles).Append(" bundles (")
              .Append(TotalBundleRefCount).Append(" refs), ")
              .Append(TotalSizeOnDisk).Append(" B on disk");
            if (TotalRuntimeMemorySize >= 0) sb.Append(", ").Append(TotalRuntimeMemorySize).Append(" B in memory");
            sb.Append('\n');

            for (int i = 0; i < Assets.Count; i++)
            {
                var a = Assets[i];
                sb.Append("  asset ").Append(a.Address).Append(" [").Append(a.TypeName).Append("] x")
                  .Append(a.RefCount).Append(" via ").Append(a.Source);
                if (a.RuntimeMemorySize >= 0) sb.Append(" (").Append(a.RuntimeMemorySize).Append(" B)");
                for (int j = 0; j < a.ByLoader.Count; j++)
                    sb.Append(' ').Append(a.ByLoader[j].Loader).Append('=').Append(a.ByLoader[j].RefCount);
                sb.Append('\n');
            }
            for (int i = 0; i < Bundles.Count; i++)
            {
                var b = Bundles[i];
                sb.Append("  bundle ").Append(b.Name).Append(' ').Append(b.Hash).Append(" x")
                  .Append(b.RefCount).Append(' ').Append(b.SizeOnDisk).Append(" B\n");
            }
            return sb.ToString();
        }

        /// <summary>A compact JSON form for dev tooling / CI logs. Hand-written (no engine serializer) so the
        /// model stays engine-free; keys mirror the row fields.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"totalLoadedAssets\":").Append(TotalLoadedAssets)
              .Append(",\"totalLoadedBundles\":").Append(TotalLoadedBundles)
              .Append(",\"totalAssetRefCount\":").Append(TotalAssetRefCount)
              .Append(",\"totalBundleRefCount\":").Append(TotalBundleRefCount)
              .Append(",\"totalSizeOnDisk\":").Append(TotalSizeOnDisk)
              .Append(",\"totalRuntimeMemorySize\":").Append(TotalRuntimeMemorySize)
              .Append(",\"byLoaderTotals\":[");
            for (int i = 0; i < ByLoaderTotals.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"loader\":").Append(Quote(ByLoaderTotals[i].Loader))
                  .Append(",\"refCount\":").Append(ByLoaderTotals[i].RefCount).Append('}');
            }
            sb.Append("],\"assets\":[");
            for (int i = 0; i < Assets.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var a = Assets[i];
                sb.Append("{\"address\":").Append(Quote(a.Address))
                  .Append(",\"type\":").Append(Quote(a.TypeName))
                  .Append(",\"refCount\":").Append(a.RefCount)
                  .Append(",\"source\":").Append(Quote(a.Source))
                  .Append(",\"runtimeMemorySize\":").Append(a.RuntimeMemorySize)
                  .Append(",\"byLoader\":[");
                for (int j = 0; j < a.ByLoader.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append("{\"loader\":").Append(Quote(a.ByLoader[j].Loader))
                      .Append(",\"refCount\":").Append(a.ByLoader[j].RefCount).Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("],\"bundles\":[");
            for (int i = 0; i < Bundles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var b = Bundles[i];
                sb.Append("{\"name\":").Append(Quote(b.Name))
                  .Append(",\"hash\":").Append(Quote(b.Hash))
                  .Append(",\"refCount\":").Append(b.RefCount)
                  .Append(",\"sizeOnDisk\":").Append(b.SizeOnDisk).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Minimal JSON string escaping for the fields a report carries (addresses, hashes, type/loader names).
        private static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
