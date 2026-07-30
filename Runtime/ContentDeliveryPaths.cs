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

        /// <summary>
        /// StreamingAssets content directory as a fetchable URL for <see cref="UnityEngine.Networking.UnityWebRequest"/>:
        /// already a URL on Android (jar:file://) / WebGL, a <c>file://</c> URL elsewhere. This is the Local origin
        /// passed to the provisioner.
        /// </summary>
        public static string StreamingAssetsContentUrl
        {
            get
            {
                string dir = StreamingAssetsContentDirectory;
                // On Android the path is already a jar URL; on WebGL it is an http(s) URL. Both carry a scheme.
                return dir.Contains("://") ? dir : "file://" + dir;
            }
        }

        /// <summary>Default disk cache for provisioned bundles (content-addressed by hash).</summary>
        public static string DefaultCacheDirectory =>
            Path.Combine(Application.persistentDataPath, ContentFolderName, "cache");
    }
}
