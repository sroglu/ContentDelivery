using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>Editor entry points for the content build. Gathers <see cref="AssetGroup"/>s and applies a build scope.</summary>
    public static class ContentDeliveryMenu
    {
        private const string OutputFolderName = "ContentBuild";

        [MenuItem("PFound/Content Delivery/Build Content (All Groups)")]
        public static void BuildAllGroups() => BuildWithScope(BuildScope.AllGroups);

        [MenuItem("PFound/Content Delivery/Build Content (Selected Groups)")]
        public static void BuildSelectedGroups() => BuildWithScope(BuildScope.OnlySelected);

        [MenuItem("PFound/Content Delivery/Build Content (Production — exclude dev-only)")]
        public static void BuildProduction() => BuildWithScope(BuildScope.ExcludeInProd);

        [MenuItem("PFound/Content Delivery/Analyze Duplicate Dependencies")]
        public static void AnalyzeDuplicates()
        {
            var duplicates = BundleDuplicateAnalyzer.Analyze(LoadAllGroups());
            if (duplicates.Count == 0)
            {
                Debug.Log("[ContentDelivery] No duplicate dependencies — no asset is implicitly pulled into more than one bundle.");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ContentDelivery] {duplicates.Count} duplicated dependenc{(duplicates.Count == 1 ? "y" : "ies")} (each embedded in every listed bundle — make it its own group to place it once):");
            foreach (var d in duplicates)
                sb.AppendLine($"  • {d.Asset}  →  {string.Join(", ", d.Bundles)}");
            Debug.LogWarning(sb.ToString());
        }

        /// <summary>Gathers all groups, narrows them to <paramref name="scope"/>, and builds for the active target.</summary>
        public static void BuildWithScope(BuildScope scope)
        {
            var selected = scope == BuildScope.OnlySelected ? SelectedGroups() : null;
            var groups = BuildScopeFilter.Apply(LoadAllGroups(), scope, selected);
            if (groups.Count == 0)
            {
                Debug.LogWarning($"[ContentDelivery] No AssetGroup in scope {scope} — nothing to build.");
                return;
            }

            string outputDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolderName);
            var report = BundleBuildPipeline.Build(groups, outputDir);

            // Post-process: a duplicate-dependency pass + a JSON build report next to the published bundles.
            var duplicates = BundleDuplicateAnalyzer.Analyze(groups);
            string reportPath = ContentBuildReportExporter.Write(report, duplicates, report.PublishDirectory);

            Debug.Log($"[ContentDelivery] Built {report.BundleCount} bundle(s) [{scope}] → {report.PublishDirectory}\nCatalog: {report.CatalogPath}\nReport: {reportPath}" +
                      (duplicates.Count > 0 ? $"\n⚠ {duplicates.Count} duplicate dependenc(ies) — see the report or run Analyze Duplicate Dependencies." : ""));
        }

        /// <summary>The <see cref="AssetGroup"/>s currently selected in the Project window.</summary>
        private static HashSet<AssetGroup> SelectedGroups()
        {
            var set = new HashSet<AssetGroup>();
            foreach (var g in Selection.GetFiltered<AssetGroup>(SelectionMode.Assets)) set.Add(g);
            return set;
        }

        /// <summary>All <see cref="AssetGroup"/> assets in the project.</summary>
        public static List<AssetGroup> LoadAllGroups()
        {
            var groups = new List<AssetGroup>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(AssetGroup)))
            {
                var group = AssetDatabase.LoadAssetAtPath<AssetGroup>(AssetDatabase.GUIDToAssetPath(guid));
                if (group != null) groups.Add(group);
            }
            return groups;
        }
    }
}
