using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// The pure, engine-free vocabulary of the <c>AssetBundles/&lt;platform&gt;/</c> content layout the app-driven
    /// catalog lifecycle uses: the well-known folder/file names, URL and path joining, base-URL token expansion,
    /// the offline test, and the predicate that decides whether a file inside a platform folder counts as a real
    /// shippable bundle. All of it is string logic with no Unity or filesystem dependency, so the Unity layer
    /// (<c>ContentPlatform</c>, <c>EmbeddedCatalogReader</c>) fills in <c>Application.*</c> paths and directory
    /// scanning around these, and the decisions stay unit-testable under mono.
    /// </summary>
    public static class AssetBundleLayout
    {
        /// <summary>Root folder (under StreamingAssets, persistentDataPath, or a CDN origin) holding per-platform content.</summary>
        public const string AssetBundlesFolder = "AssetBundles";

        /// <summary>
        /// Small text file inside a platform folder naming the catalog file that ships alongside it. PFound's own
        /// pointer-file name — the build pipeline writes it, the embedded reader reads it, so the two stay in step.
        /// </summary>
        public const string EmbeddedCatalogPointerFileName = "embedded-catalog-pointer.txt";

        /// <summary>
        /// Fixed pointer file the remote publishes so the app can discover the current catalog file name without a
        /// directory listing. PFound's own name (the publish step writes it, the resolver reads it).
        /// </summary>
        public const string RemoteCatalogPointerFileName = "remote-catalog-pointer.txt";

        /// <summary>The key in the remote pointer file whose value is the current catalog file name.</summary>
        public const string CatalogFileNameKey = "catalogFileName";

        // ----- URL / path joining (kept identical so writer and reader never disagree) -----

        /// <summary>Joins a base and a segment with a single '/', tolerating a trailing slash on the base.</summary>
        public static string CombineUrl(string baseUrl, string segment)
        {
            if (string.IsNullOrEmpty(baseUrl)) return segment ?? string.Empty;
            if (string.IsNullOrEmpty(segment)) return baseUrl;
            bool hasSlash = baseUrl[baseUrl.Length - 1] == '/';
            return hasSlash ? baseUrl + segment : baseUrl + "/" + segment;
        }

        /// <summary>The <c>AssetBundles/&lt;platform&gt;</c> tail appended to any root (URL or local base).</summary>
        public static string PlatformSubPath(string platformDirectory) =>
            CombineUrl(AssetBundlesFolder, platformDirectory);

        // ----- base-URL token expansion (capability 5) -----

        /// <summary>
        /// Expands the three supported tokens in a configured base URL: <c>{PROJECT}</c>, <c>{persistentDataPath}</c>
        /// and <c>{streamingAssetsPath}</c>. The caller supplies the runtime values (the Unity layer passes
        /// <c>Application.*</c>). A token whose value is null is left untouched.
        /// </summary>
        public static string ExpandTokens(string baseUrl, string project, string persistentDataPath, string streamingAssetsPath)
        {
            if (string.IsNullOrEmpty(baseUrl)) return baseUrl;
            string result = baseUrl;
            if (project != null) result = result.Replace("{PROJECT}", project);
            if (persistentDataPath != null) result = result.Replace("{persistentDataPath}", persistentDataPath);
            if (streamingAssetsPath != null) result = result.Replace("{streamingAssetsPath}", streamingAssetsPath);
            return result;
        }

        /// <summary>An empty (post-expansion) active base URL means "no remote origin configured" — offline mode.</summary>
        public static bool IsOfflineBaseUrl(string activeBaseUrl) => string.IsNullOrEmpty(activeBaseUrl);

        // ----- embedded-bundle presence (capability 5) -----

        /// <summary>
        /// Whether a file name inside a platform folder is a real shippable bundle payload (so a folder that holds one
        /// counts as carrying embedded content). Rejects the non-payload sidecars: Unity meta files, the two pointer
        /// files, and any catalog artifact (<c>catalog*</c>). The argument may be a bare name or a full path — only the
        /// leaf is judged.
        /// </summary>
        public static bool IsShippableBundle(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string leaf = LeafName(fileName);
            if (leaf.Length == 0) return false;

            bool isSidecar =
                leaf.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("catalog", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, EmbeddedCatalogPointerFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, RemoteCatalogPointerFileName, StringComparison.OrdinalIgnoreCase);
            return !isSidecar;
        }

        private static string LeafName(string path)
        {
            int slash = path.LastIndexOfAny(s_separators);
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private static readonly char[] s_separators = { '/', '\\' };
    }
}
