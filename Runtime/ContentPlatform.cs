using System.IO;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// The Unity side of the <c>AssetBundles/&lt;platform&gt;/</c> layout: it resolves the active platform folder,
    /// builds the embedded (StreamingAssets) and downloaded (persistentDataPath) roots for it, detects whether
    /// real bundles ship embedded, and expands the configured base-URL tokens with the live <c>Application.*</c>
    /// paths. All the string decisions live in the engine-free <see cref="AssetBundleLayout"/>; this type only
    /// supplies Unity's paths and the directory scan around them.
    /// </summary>
    public static class ContentPlatform
    {
        /// <summary>
        /// The folder name for the platform content is built/loaded for. In the editor it maps the active build
        /// target (so authoring writes to the same folder a build would); in a player it maps the runtime platform.
        /// </summary>
        public static string ActivePlatformFolder()
        {
#if UNITY_EDITOR
            return EditorPlatformFolder(UnityEditor.EditorUserBuildSettings.activeBuildTarget);
#else
            return RuntimePlatformFolder(Application.platform);
#endif
        }

        /// <summary>persistentDataPath/AssetBundles/&lt;platform&gt; — where downloaded (remote) bundles are cached.</summary>
        public static string GetRemoteAssetBundlePath(string platformFolder = null) =>
            Path.Combine(Application.persistentDataPath, AssetBundleLayout.AssetBundlesFolder, platformFolder ?? ActivePlatformFolder());

        /// <summary>streamingAssetsPath/AssetBundles/&lt;platform&gt; — where build-embedded (local) bundles ship.</summary>
        public static string GetEmbeddedAssetBundlePath(string platformFolder = null) =>
            Path.Combine(Application.streamingAssetsPath, AssetBundleLayout.AssetBundlesFolder, platformFolder ?? ActivePlatformFolder());

        /// <summary>
        /// Whether real shippable bundles are embedded for <paramref name="platformFolder"/>: scans the embedded
        /// folder and returns true only if at least one file is an actual bundle payload (meta files, catalog
        /// artifacts and the pointer files don't count — see <see cref="AssetBundleLayout.IsShippableBundle"/>).
        /// Platforms whose StreamingAssets is not an enumerable directory (Android jar, WebGL) report false.
        /// </summary>
        public static bool HasEmbeddedBundles(string platformFolder = null)
        {
            string dir = GetEmbeddedAssetBundlePath(platformFolder);
            if (!Directory.Exists(dir)) return false;
            foreach (string path in Directory.GetFiles(dir))
                if (AssetBundleLayout.IsShippableBundle(path)) return true;
            return false;
        }

        /// <summary>
        /// Expands <c>{PROJECT}</c> / <c>{persistentDataPath}</c> / <c>{streamingAssetsPath}</c> in a configured base
        /// URL using the live application values. An empty result is the offline signal (<see cref="IsOffline"/>).
        /// </summary>
        public static string ExpandBaseUrl(string baseUrl) =>
            AssetBundleLayout.ExpandTokens(baseUrl, Application.productName, Application.persistentDataPath, Application.streamingAssetsPath);

        /// <summary>An empty (post-expansion) active base URL means offline — nothing to fetch.</summary>
        public static bool IsOffline(string activeBaseUrl) => AssetBundleLayout.IsOfflineBaseUrl(activeBaseUrl);

        // Runtime platform → Unity's standard bundle-folder name (aligned with BuildTarget.ToString()).
        private static string RuntimePlatformFolder(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.WebGLPlayer: return "WebGL";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor: return "StandaloneOSX";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor: return "StandaloneWindows64";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor: return "StandaloneLinux64";
                default: return platform.ToString();
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// A build target → the same folder names the runtime map produces, so authoring and player agree. Public
        /// so editor build configs can resolve the platform folder for an explicitly-chosen target.
        /// </summary>
        public static string EditorPlatformFolder(UnityEditor.BuildTarget target)
        {
            switch (target)
            {
                case UnityEditor.BuildTarget.Android: return "Android";
                case UnityEditor.BuildTarget.iOS: return "iOS";
                case UnityEditor.BuildTarget.WebGL: return "WebGL";
                case UnityEditor.BuildTarget.StandaloneOSX: return "StandaloneOSX";
                case UnityEditor.BuildTarget.StandaloneWindows:
                case UnityEditor.BuildTarget.StandaloneWindows64: return "StandaloneWindows64";
                case UnityEditor.BuildTarget.StandaloneLinux64: return "StandaloneLinux64";
                default: return target.ToString();
            }
        }
#endif
    }
}
