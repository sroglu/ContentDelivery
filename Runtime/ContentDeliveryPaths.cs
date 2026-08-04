using System.IO;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Well-known on-device locations the build pipeline and the runtime must agree on. Local
    /// (build-shipped) bundles and the bootstrap catalog live under a single subfolder of
    /// StreamingAssets; the disk cache for provisioned bundles lives under persistentDataPath.
    /// </summary>
    public static class ContentDeliveryPaths
    {
        /// <summary>Subfolder of StreamingAssets that holds Local bundle files (the runner stages them onward into the
        /// runtime-consumed <c>AssetBundles/&lt;platform&gt;/</c> layout, which also holds the single .lzma catalog).</summary>
        public const string ContentFolderName = "PFoundContent";

        /// <summary>StreamingAssets content directory as a filesystem path (editor/build authoring side).</summary>
        public static string StreamingAssetsContentDirectory =>
            Path.Combine(Application.streamingAssetsPath, ContentFolderName);

        // NOTE: there is deliberately no "content URL" helper here. The Local-bundle origin is
        // ContentPlatform.GetEmbeddedAssetBundleUrl(), which is derived from GetEmbeddedAssetBundlePath() so the
        // staged layout has exactly ONE definition. A second URL rooted at this directory used to exist and silently
        // omitted the AssetBundles/<platform> segment the runner actually stages into — every Local bundle 404'd on
        // device while the editor fast-path masked it. Don't reintroduce a second root; extend ContentPlatform.

        /// <summary>Default disk cache for provisioned bundles (content-addressed by hash).</summary>
        public static string DefaultCacheDirectory =>
            Path.Combine(Application.persistentDataPath, ContentFolderName, "cache");
    }
}
