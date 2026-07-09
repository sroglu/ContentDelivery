using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PFound.Compression;
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

        /// <summary>The raw build output directory (<c>&lt;projectRoot&gt;/ContentBuild</c>) — bundles + catalog before staging.</summary>
        public static string OutputDirectory => Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolderName);

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

        /// <summary>Builds content per <paramref name="config"/> (all project groups) and stages the embedded package.</summary>
        public static ContentBuildReport Build(CatalogEditorConfig config)
        {
            var groups = GatherGroups(config);
            return Build(config, groups);
        }

        /// <summary>Builds an EXPLICIT group set (the manifest is the caller); config supplies platform/env/offline.</summary>
        public static ContentBuildReport Build(CatalogEditorConfig config, IReadOnlyList<AssetGroup> groups)
        {
            // Production drops dev-only groups (AssetGroup.ExcludeInProduction) HERE — the single choke, so BOTH the
            // manifest build and the raw Build(config) / App Build menu honor config.Mode uniformly (dev-only content
            // never leaks into a production build regardless of which entry point ran).
            if (groups != null && config.Mode == BuildMode.Production)
                groups = BuildScopeFilter.Apply(groups, BuildScope.ExcludeInProd);

            if (groups == null || groups.Count == 0)
            {
                Debug.LogWarning("[ContentDelivery] No groups to build.");
                return default;
            }

            // Monotonic version stamp: each App Build gets a fresh build number, so CatalogFileName() (used by
            // StageEmbeddedPackage + the pointer) names a strictly-newer catalog than the last one.
            // Offline packages must be directly loadable from StreamingAssets, so build UNCOMPRESSED (Unity LZ4);
            // online builds keep the LZMA transfer form the runtime decompresses into its cache.
            var compression = config.OfflineBuild ? BundleCompression.None : BundleCompression.Lzma;

            string outputDir = OutputDirectory;
            var report = BundleBuildPipeline.Build(groups, outputDir, config.BuildPlatform.ToBuildTarget(), hasher: null, compression: compression);

            // Offline forces every bundle Local (the offline catalog has zero remote entries); otherwise the
            // pipeline's catalog passes through.
            Catalog parsed = CatalogJson.Parse(report.CatalogJson);
            if (config.OfflineBuild) parsed = ForceAllLocal(parsed);

            config.CommitBuildNumber(config.ResolveNextBuildNumber());   // finalize the build number first

            // ONE SOURCE: the catalog's content metadata is filled from the SAME config values that compose the file
            // name (CatalogFileName) — so the name tokens and the content fields can never disagree.
            Catalog catalog = new Catalog(
                parsed.AllBundles, parsed.AllAssets, parsed.ContentHash, parsed.AllPacks,
                appVersion: config.AppVersion(), buildNumber: config.CatalogBuildNumber,
                platform: config.PlatformFolder(), mode: config.ModeToken());

            StageEmbeddedPackage(report, catalog, config);

            AssetDatabase.Refresh();
            string mode = config.Mode == BuildMode.Production ? "prod" : "dev";
            Debug.Log($"[ContentDelivery] Built {report.BundleCount} bundle(s) [{groups.Count} group(s), {mode}, {(config.OfflineBuild ? "OFFLINE" : "online")}] → embedded {config.PlatformFolder()}.");
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

        // Writes the runtime-consumed layout: every embedded bundle by its hash, the single binary .lzma catalog +
        // its pointer in the embedded package, and (online) the same catalog into the publish/CDN dir.
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

            // Clear stale catalog artifacts first (old hash names / formats, or a legacy builder's catalog left in
            // this shared folder) so the embedded package holds EXACTLY ONE catalog — the one the pointer names.
            // Bundles are hash-named (never start with "catalog"), so they are untouched; the pointer is rewritten below.
            foreach (var stale in Directory.GetFiles(embeddedDir))
                if (Path.GetFileName(stale).StartsWith("catalog", System.StringComparison.OrdinalIgnoreCase))
                    File.Delete(stale);

            // ONE catalog file, produced HERE — the single catalog-file producer for the whole pipeline. PFound binary
            // form, LZMA-compressed, named with its content hash last: catalog_<gameId>_v<ver>_b<build>_<dev|prod>_<hash>.lzma.
            // The SAME bytes go to the embedded package AND (online) the publish/CDN dir, so no .json catalog exists
            // anywhere. Runtime reads it via CatalogCodec (auto-detects LZMA → PFCB binary).
            byte[] stored = Lzma.Compress(CatalogBinary.Write(catalog));
            string catalogFileName = config.CatalogFileName(new XxHash3ContentHasher().Compute(stored));

            // Embedded package (StreamingAssets): the catalog + a bare-name pointer the embedded reader consumes.
            File.WriteAllBytes(Path.Combine(embeddedDir, catalogFileName), stored);
            File.WriteAllText(Path.Combine(embeddedDir, AssetBundleLayout.EmbeddedCatalogPointerFileName), catalogFileName);

            // Publish/CDN dir (online only): the SAME single .lzma catalog beside the remote bundles, plus a remote
            // pointer naming it — so the runtime resolver discovers the catalog by name (RemoteCatalogPointerReader),
            // never having to predict the hash-stamped file name.
            if (!config.OfflineBuild && !string.IsNullOrEmpty(report.PublishDirectory))
            {
                Directory.CreateDirectory(report.PublishDirectory);
                File.WriteAllBytes(Path.Combine(report.PublishDirectory, catalogFileName), stored);
                File.WriteAllText(
                    Path.Combine(report.PublishDirectory, AssetBundleLayout.RemoteCatalogPointerFileName),
                    $"{AssetBundleLayout.CatalogFileNameKey}={catalogFileName}");
            }
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
            return new Catalog(bundles, new List<CatalogAsset>(catalog.AllAssets), catalog.ContentHash, new List<CatalogPack>(catalog.AllPacks));
        }

        // ----- group gathering -----

        private static List<AssetGroup> GatherGroups(CatalogEditorConfig config) => ContentDeliveryMenu.LoadAllGroups();

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
