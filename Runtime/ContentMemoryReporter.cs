using System.Collections.Generic;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Captures a <see cref="ContentMemoryReport"/> from the live runtime: loaded assets from
    /// <see cref="AssetManager"/> plus loaded bundles from every registered <see cref="IBundleMemorySource"/>.
    /// A dev/diagnostics entry point — call it to dump what content is resident and why.
    /// </summary>
    public static class ContentMemoryReporter
    {
        /// <summary>
        /// Snapshots resident assets and bundles into a report. The default pass is cheap (counts, ref-counts,
        /// and the on-disk size of each loaded bundle's cache file). Pass <paramref name="deep"/> to additionally
        /// measure each asset's runtime memory via the Profiler — costly, and only yields real numbers in the
        /// editor / development builds, so it is off by default.
        /// </summary>
        public static ContentMemoryReport Capture(bool deep = false)
        {
            var assets = AssetManager.SnapshotAssetRows(deep);

            var bundles = new List<BundleMemoryRow>();
            var sources = AssetManager.Sources;
            for (int i = 0; i < sources.Count; i++)
                if (sources[i] is IBundleMemorySource bundleSource)
                    bundles.AddRange(bundleSource.SnapshotBundleRows());

            return ContentMemoryReport.From(assets, bundles);
        }
    }
}
