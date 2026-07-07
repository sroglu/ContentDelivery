using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// The coordinates that locate remote content, bundled into one value so callers pass a single config rather than
    /// a loose (origin, platform, catalog) triple. Immutable for the run: the CDN origin, the per-platform folder that
    /// hangs under <c>AssetBundles/</c>, and the catalog file name (with extension). <see cref="ResolveContentUrl"/>
    /// is the folder every bundle and the catalog are fetched from. Pure Core (engine-free) — unit-testable without
    /// Unity.
    /// </summary>
    public readonly struct RemoteContentConfig
    {
        /// <summary>CDN origin, e.g. <c>https://cdn.example.com/game</c>. May carry tokens the platform layer expands.</summary>
        public readonly string OriginUrl;

        /// <summary>Platform folder under <c>AssetBundles/</c> (e.g. <c>Android</c>, <c>iOS</c>, <c>StandaloneOSX</c>).</summary>
        public readonly string PlatformFolder;

        /// <summary>Catalog file name including its extension (e.g. <c>catalog_v42.json</c>).</summary>
        public readonly string CatalogFileName;

        public RemoteContentConfig(string originUrl, string platformFolder, string catalogFileName)
        {
            OriginUrl = originUrl;
            PlatformFolder = platformFolder;
            CatalogFileName = catalogFileName;
        }

        /// <summary>The remote folder these bundles + catalog live in: <c>{OriginUrl}/AssetBundles/{PlatformFolder}</c>.</summary>
        public string ResolveContentUrl() =>
            AssetBundleLayout.CombineUrl(AssetBundleLayout.CombineUrl(OriginUrl, AssetBundleLayout.AssetBundlesFolder), PlatformFolder);

        /// <summary>The full URL of the catalog file itself, under <see cref="ResolveContentUrl"/>.</summary>
        public string GetCatalogUrl() =>
            AssetBundleLayout.CombineUrl(ResolveContentUrl(), CatalogFileName);

        /// <summary>The full URL of a stored object (bundle) named <paramref name="storedObjectName"/>.</summary>
        public string GetBundleUrl(string storedObjectName) =>
            AssetBundleLayout.CombineUrl(ResolveContentUrl(), storedObjectName);

        /// <summary>
        /// An empty <see cref="OriginUrl"/> is the offline signal: there is no remote origin to fetch from
        /// (see <see cref="AssetBundleLayout.IsOfflineBaseUrl"/>).
        /// </summary>
        public bool IsOffline => AssetBundleLayout.IsOfflineBaseUrl(OriginUrl);

        /// <summary>
        /// Whether this config can actually drive a remote fetch: it needs an origin (i.e. not offline) plus a platform
        /// folder and a catalog file name to point at.
        /// </summary>
        public bool IsUsable =>
            !IsOffline
            && !string.IsNullOrEmpty(PlatformFolder)
            && !string.IsNullOrEmpty(CatalogFileName);
    }
}
