using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// One-click Build / Clear for the app-driven content package, driven by a <see cref="CatalogEditorConfig"/>.
    /// It reuses the existing <see cref="BundleBuildPipeline"/> (SBP) for the heavy build, then stages the
    /// <c>StreamingAssets/AssetBundles/&lt;platform&gt;/</c> embedded layout the runtime reader consumes: the
    /// build-embedded bundles, the catalog under the config's file name, and the embedded-catalog pointer file
    /// (<see cref="AssetBundleLayout.EmbeddedCatalogPointerFileName"/>).
    /// The <see cref="CatalogEditorConfig.OfflineBuild"/> switch forces every bundle Local (uncompressed so
    /// it is directly <c>LoadFromFile</c>-able) and rewrites the catalog to zero remote entries. Editor-only.
    /// Upload is delegated to an app-provided <see cref="ICdnUploader"/> (the config's index selects which).
    /// </summary>
    public static class CatalogEditorBuildRunner
    {
        private const string OutputFolderName = "ContentBuild";

        [MenuItem("PFound/Content Delivery/App Build (from Catalog Editor Config)")]
        public static void BuildFromConfigMenu()
        {
            var config = FindConfig();
            if (config == null)
            {
                Debug.LogWarning("[ContentDelivery] No CatalogEditorConfig asset found — create one (PFound/Content Delivery/Catalog Editor Config).");
                return;
            }
            Build(config);
        }

        [MenuItem("PFound/Content Delivery/App Clear Embedded Package")]
        public static void ClearEmbeddedMenu()
        {
            var config = FindConfig();
            string platform = config != null ? config.PlatformFolder() : ContentPlatform.ActivePlatformFolder();
            ClearEmbedded(platform);
        }

        /// <summary>Builds content per <paramref name="config"/> and stages the embedded package. Returns the report.</summary>
        public static ContentBuildReport Build(CatalogEditorConfig config)
        {
            var groups = GatherGroups(config);
            if (groups.Count == 0)
            {
                Debug.LogWarning($"[ContentDelivery] No AssetGroup in scope {config.Scope} — nothing to build.");
                return default;
            }

            // Monotonic version stamp: each App Build gets a fresh build number, so CatalogFileName() (used by
            // StageEmbeddedPackage + the pointer) names a strictly-newer catalog than the last one.
            config.BumpBuildNumber();

            // Offline packages must be directly loadable from StreamingAssets, so build UNCOMPRESSED (Unity LZ4);
            // online builds keep the LZMA transfer form the runtime decompresses into its cache.
            var compression = config.OfflineBuild ? BundleCompression.None : BundleCompression.Lzma;

            string outputDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolderName);
            var report = BundleBuildPipeline.Build(groups, outputDir, config.BuildPlatform, hasher: null, compression: compression);

            Catalog catalog = CatalogJson.Parse(report.CatalogJson);
            if (config.OfflineBuild) catalog = ForceAllLocal(catalog);

            StageEmbeddedPackage(report, catalog, config);

            AssetDatabase.Refresh();
            Debug.Log($"[ContentDelivery] App build [{(config.OfflineBuild ? "OFFLINE" : "online")}] " +
                      $"{report.BundleCount} bundle(s) → embedded {config.PlatformFolder()} package.");
            return report;
        }

        /// <summary>Publishes the online (remote) portion of a prior build via an app-provided uploader.</summary>
        public static async System.Threading.Tasks.Task UploadAsync(CatalogEditorConfig config, ICdnUploader uploader, string publishDirectory)
        {
            if (config.OfflineBuild) { Debug.Log("[ContentDelivery] OfflineBuild — nothing to upload."); return; }
            await CdnUpload.UploadPublishDirectoryAsync(uploader, publishDirectory);
        }

        /// <summary>Removes the staged embedded package for a platform (bundles + catalog + pointer).</summary>
        public static void ClearEmbedded(string platformFolder)
        {
            string dir = Path.Combine(Application.streamingAssetsPath, AssetBundleLayout.AssetBundlesFolder, platformFolder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string meta = dir + ".meta";
            if (File.Exists(meta)) File.Delete(meta);
            AssetDatabase.Refresh();
            Debug.Log($"[ContentDelivery] Cleared embedded package: {dir}");
        }

        // ----- staging -----

        // Writes the runtime-consumed embedded layout: every embedded bundle by its hash, the catalog under the
        // config's file name, and the pointer file naming that catalog.
        private static void StageEmbeddedPackage(ContentBuildReport report, Catalog catalog, CatalogEditorConfig config)
        {
            string embeddedDir = Path.Combine(Application.streamingAssetsPath, AssetBundleLayout.AssetBundlesFolder, config.PlatformFolder());
            Directory.CreateDirectory(embeddedDir);

            foreach (var entry in report.Bundles)
            {
                // Offline promotes every bundle to embedded; online embeds only the Local ones.
                if (!config.OfflineBuild && !entry.Local) continue;
                string source = Path.Combine(entry.Local ? report.StreamingAssetsDirectory : report.PublishDirectory, entry.Hash);
                if (File.Exists(source)) File.Copy(source, Path.Combine(embeddedDir, entry.Hash), true);
            }

            string catalogFileName = config.CatalogFileName();
            File.WriteAllText(Path.Combine(embeddedDir, catalogFileName), CatalogJson.ToJson(
                new List<CatalogBundle>(catalog.AllBundles), new List<CatalogAsset>(catalog.AllAssets),
                new List<CatalogPack>(catalog.AllPacks), catalog.Version));

            File.WriteAllText(Path.Combine(embeddedDir, AssetBundleLayout.EmbeddedCatalogPointerFileName), catalogFileName);
        }

        // Rewrites every bundle as Local — the offline catalog has zero remote entries.
        private static Catalog ForceAllLocal(Catalog catalog)
        {
            var bundles = new List<CatalogBundle>();
            foreach (var b in catalog.AllBundles)
                bundles.Add(new CatalogBundle
                {
                    Name = b.Name, Hash = b.Hash, UncompressedHash = b.UncompressedHash,
                    Dependencies = b.Dependencies, Local = true, Compression = b.Compression,
                });
            return new Catalog(bundles, new List<CatalogAsset>(catalog.AllAssets), catalog.Version, new List<CatalogPack>(catalog.AllPacks));
        }

        // ----- group gathering -----

        private static List<AssetGroup> GatherGroups(CatalogEditorConfig config)
        {
            var all = ContentDeliveryMenu.LoadAllGroups();
            ICollection<AssetGroup> selected = config.Scope == BuildScope.OnlySelected ? config.GroupsToBuild : null;
            return BuildScopeFilter.Apply(all, config.Scope, selected);
        }

        private static CatalogEditorConfig FindConfig()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(CatalogEditorConfig)))
            {
                var cfg = AssetDatabase.LoadAssetAtPath<CatalogEditorConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (cfg != null) return cfg;
            }
            return null;
        }
    }
}
