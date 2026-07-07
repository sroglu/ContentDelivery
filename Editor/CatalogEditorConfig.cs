using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>One content environment: a name and its CDN origin (may carry {PROJECT}/{persistentDataPath} tokens).</summary>
    [System.Serializable]
    public struct ContentEnvironmentEntry
    {
        public string Name;
        public string BaseUrl;
    }

    /// <summary>
    /// The editor build-configuration for content delivery, centered on the <see cref="OfflineBuild"/> master
    /// switch:
    /// <list type="bullet">
    ///   <item><b>OfflineBuild = true</b> — every group is forced-embedded into StreamingAssets and the catalog is
    ///   emitted with zero remote entries: a fully self-contained, no-CDN package.</item>
    ///   <item><b>OfflineBuild = false</b> — per-group distribution is honored; remote groups are staged for upload.</item>
    /// </list>
    /// It also carries the build platform, scope, group selection, game id, upload target, and the shared content
    /// environments — one authored source so the runtime remote config and the build never drift. Editor-only SO.
    /// </summary>
    [CreateAssetMenu(menuName = "PFound/Content Delivery/Catalog Editor Config", fileName = "CatalogEditorConfig")]
    public sealed class CatalogEditorConfig : ScriptableObject
    {
        [Header("Master switch")]
        [Tooltip("Force ALL groups into StreamingAssets and strip remote entries from the catalog (offline package).")]
        public bool OfflineBuild = false;

        [Header("Build")]
        public BuildTarget BuildPlatform = BuildTarget.Android;
        [Tooltip("Which groups the build includes.")]
        public BuildScope Scope = BuildScope.AllGroups;
        [Tooltip("Groups built when Scope = OnlySelected.")]
        public List<AssetGroup> GroupsToBuild = new List<AssetGroup>();
        [Tooltip("Game identifier folded into the catalog file name / upload path.")]
        public string GameId = "game";

        [Header("Environments (shared source — no drift)")]
        public List<ContentEnvironmentEntry> Environments = new List<ContentEnvironmentEntry>();
        [Tooltip("The active environment name; must be one of Environments above.")]
        public string ActiveEnvironment = "prod";

        [Header("Upload")]
        [Tooltip("Authored gate for the post-build upload step (OFF = build only). When ON, UploadTargetIndex applies.")]
        public bool UploadAfterBuild = false;
        [Tooltip("Index into an app-provided uploader list; applies only when UploadAfterBuild is ON.")]
        public int UploadTargetIndex = 0;

        /// <summary>The active environment's base URL, or empty when offline / unconfigured.</summary>
        public string ActiveBaseUrl()
        {
            if (OfflineBuild) return string.Empty; // offline package has no remote origin
            for (int i = 0; i < Environments.Count; i++)
                if (string.Equals(Environments[i].Name, ActiveEnvironment, System.StringComparison.Ordinal))
                    return Environments[i].BaseUrl;
            return string.Empty;
        }

        /// <summary>The catalog file name (with extension) this config publishes, folding in the game id.</summary>
        public string CatalogFileName() => "catalog_" + GameId + ".json";

        /// <summary>The platform folder name for <see cref="BuildPlatform"/> (matches the runtime layout).</summary>
        public string PlatformFolder() => ContentPlatform.EditorPlatformFolder(BuildPlatform);

        /// <summary>The runtime remote config this build targets (empty base URL = offline).</summary>
        public RemoteContentConfig ToRemoteConfig() =>
            new RemoteContentConfig(ActiveBaseUrl(), PlatformFolder(), CatalogFileName());
    }
}
