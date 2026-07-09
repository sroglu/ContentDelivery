using System;
using System.Collections.Generic;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEngine;
using PFound.ContentDelivery;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>A named set of content groups — a generic grouping key (e.g. a game id), NOT a game reference.</summary>
    [Serializable]
    public class ContentSet
    {
        public string Id;                                   // generic key; no game/"mini-game" meaning here
        public List<AssetGroup> Groups = new List<AssetGroup>();
    }

    /// <summary>Which sets a build includes.</summary>
    public enum BuildSelectionMode { AllSets, SingleSet }

    /// <summary>
    /// Curated build registry: named content-group sets + a core set built every time. The single set-based build
    /// entry point (resolve a scope → exact group list → build). Generic: PFound has no game vocabulary — a game
    /// project maps its own ids onto <see cref="ContentSet.Id"/> in its GameSpecific layer.
    /// </summary>
    [CreateAssetMenu(menuName = "PFound/Content Delivery/Content Build Manifest", fileName = "ContentBuildManifest")]
    public sealed class ContentBuildManifest : ScriptableObject, IContentAuthoringValidation
    {
        [InfoBox("@AuthoringValidation.Errors(this)", InfoMessageType.Error, "@AuthoringValidation.HasErrors(this)")]
        [InfoBox("@AuthoringValidation.Warnings(this)", InfoMessageType.Warning, "@AuthoringValidation.HasWarnings(this)")]
        [InfoBox("Ready — no manifest issues.", InfoMessageType.Info, "@AuthoringValidation.Ready(this)")]
        [FoldoutGroup("Sets", expanded: true)]
        public List<ContentSet> Sets = new List<ContentSet>();

        [FoldoutGroup("Always Included (core — every build)")]
        public List<AssetGroup> AlwaysIncluded = new List<AssetGroup>();

        [FoldoutGroup("Build", expanded: true), Required]
        public CatalogEditorConfig Config;

        [FoldoutGroup("Build")]
        public BuildSelectionMode Selection = BuildSelectionMode.AllSets;

        [FoldoutGroup("Build"), ShowIf("@Selection == BuildSelectionMode.SingleSet"), CustomValueDrawer(nameof(DrawSetId))]
        public string SelectedSetId;   // used when Selection == SingleSet

        /// <summary>
        /// The exact group list for a scope: AllSets → union of every set's groups; SingleSet → the named set's
        /// groups. Both ∪ <see cref="AlwaysIncluded"/>, de-duplicated by asset identity, nulls dropped.
        /// Throws <see cref="ArgumentException"/> when SingleSet names an unknown set id.
        /// </summary>
        public static List<AssetGroup> ResolveGroups(ContentBuildManifest manifest, BuildSelectionMode mode, string setId = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            IEnumerable<AssetGroup> setGroups;
            if (mode == BuildSelectionMode.AllSets)
                setGroups = manifest.Sets.SelectMany(s => s.Groups);
            else
            {
                var set = manifest.Sets.FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.Ordinal));
                if (set == null) throw new ArgumentException($"No ContentSet with Id '{setId}'.", nameof(setId));
                setGroups = set.Groups;
            }

            var seen = new HashSet<AssetGroup>();
            var result = new List<AssetGroup>();
            foreach (var g in setGroups.Concat(manifest.AlwaysIncluded))
                if (g != null && seen.Add(g)) result.Add(g);
            return result;
        }

        /// <summary>
        /// Resolves the current scope and hands the exact group list to
        /// <see cref="CatalogEditorBuildRunner.Build(CatalogEditorConfig, IReadOnlyList{AssetGroup})"/>. The
        /// production dev-only filter (<see cref="BuildMode.Production"/> → <see cref="AssetGroup.ExcludeInProduction"/>)
        /// lives inside that shared runner, so it applies uniformly to every build entry point — not here.
        /// </summary>
        [FoldoutGroup("Build"), Button("Build (resolve scope → CatalogEditorBuildRunner)", ButtonSizes.Medium)]
        void BuildSelected()
        {
            if (!Config) { Debug.LogError("[ContentDelivery] Manifest has no CatalogEditorConfig — assign one."); return; }
            var groups = ResolveGroups(this, Selection, SelectedSetId);
            CatalogEditorBuildRunner.Build(Config, groups);   // Mode→ExcludeInProd applied inside the shared runner
        }

        /// <summary>
        /// Inspector self-validation: (1) buildability — every entry in the resolved scope has an Asset and a
        /// unique, non-empty Address; (2) orphan — a project AssetGroup covered by no set and not AlwaysIncluded
        /// never builds; (3) drift — the resolved scope's addresses vs the last embedded catalog for this platform.
        /// </summary>
        public IEnumerable<AuthoringIssue> Validate()
        {
            // Guard the one precondition that would make ResolveGroups throw (unknown SingleSet id) EXPLICITLY —
            // don't try/catch our own throw. If it holds, ResolveGroups can't throw, so no defensive wrapping.
            if (Selection == BuildSelectionMode.SingleSet &&
                !Sets.Any(s => string.Equals(s.Id, SelectedSetId, StringComparison.Ordinal)))
            {
                yield return AuthoringIssue.Error($"No ContentSet with Id '{SelectedSetId}'.");
                yield break;
            }
            var resolved = ResolveGroups(this, Selection, SelectedSetId);

            // 1) Buildability gate: every resolved entry valid + addresses unique across the set.
            var seenAddr = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in resolved)
                foreach (var e in g.Entries)
                {
                    string where = $"{g.name}/{(e.Asset ? e.Asset.name : "?")}";
                    if (!e.Asset) yield return AuthoringIssue.Error($"[{where}] no Asset assigned.");
                    if (string.IsNullOrWhiteSpace(e.Address)) yield return AuthoringIssue.Error($"[{where}] empty Address.");
                    else if (!seenAddr.Add(e.Address)) yield return AuthoringIssue.Error($"Duplicate Address '{e.Address}' across the resolved set.");
                }

            // 2) Orphan: project AssetGroups covered by NO set and not AlwaysIncluded → never build.
            var covered = new HashSet<AssetGroup>(Sets.SelectMany(s => s.Groups).Concat(AlwaysIncluded).Where(x => x));
            foreach (var g in ContentDeliveryMenu.LoadAllGroups())
                if (!covered.Contains(g))
                    yield return AuthoringIssue.Warning($"AssetGroup '{g.name}' is in no set and not AlwaysIncluded — it will never build.");

            // 3) Drift vs last embedded build (informational): selected addresses AND build mode vs the last catalog.
            string platform = Config ? Config.PlatformFolder() : ContentPlatform.ActivePlatformFolder();
            var lastBuilt = ReadEmbeddedCatalog(platform);
            if (lastBuilt != null)
            {
                var built = lastBuilt.AllAssets.Select(a => a.Address).Where(a => !string.IsNullOrEmpty(a)).ToHashSet(StringComparer.Ordinal);
                var selectedAddrs = resolved.SelectMany(g => g.Entries).Select(e => e.Address).Where(a => !string.IsNullOrEmpty(a)).ToHashSet(StringComparer.Ordinal);
                foreach (var a in selectedAddrs) if (!built.Contains(a)) yield return AuthoringIssue.Error($"Selected but not in the last build: '{a}' — rebuild.");
                foreach (var a in built) if (!selectedAddrs.Contains(a)) yield return AuthoringIssue.Warning($"Last build has extra content beyond the selected scope: '{a}' (harmless, wider than expected).");

                // Build-mode drift: the last embedded build's stamped mode vs the config's current Mode.
                if (Config && !string.IsNullOrEmpty(lastBuilt.BuildMode))
                {
                    string cur = Config.Mode == BuildMode.Production ? "production" : "development";
                    if (!string.Equals(lastBuilt.BuildMode, cur, StringComparison.OrdinalIgnoreCase))
                        yield return AuthoringIssue.Warning($"Last embedded build was '{lastBuilt.BuildMode}' but Config.Mode is now '{cur}' — rebuild to match.");
                }
            }
        }

        // Reads the last embedded catalog for a platform, or null if none built. Editor-only desktop
        // StreamingAssets file read — sync-over-async is acceptable at this boundary.
        static Catalog ReadEmbeddedCatalog(string platform)
        {
            if (!ContentPlatform.HasEmbeddedBundles(platform)) return null;
            var res = EmbeddedCatalogReader.TryReadEmbeddedCatalogAsync(platform).GetAwaiter().GetResult();
            return res.Found ? res.Catalog : null;
        }

        // Plain-popup drawer for the set id (avoid Odin's broken selector window on this Unity version).
        string DrawSetId(string value, GUIContent label)
        {
            var ids = Sets.Select(s => s.Id).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (ids.Length == 0) { UnityEditor.EditorGUILayout.LabelField(label, new GUIContent("(add a set with an Id)")); return value; }
            int cur = Array.IndexOf(ids, value);
            if (cur >= 0) { int p = UnityEditor.EditorGUILayout.Popup(label.text, cur, ids); return p != cur ? ids[p] : value; }
            var opts = new[] { $"⚠ '{value}'" }.Concat(ids).ToArray();
            int q = UnityEditor.EditorGUILayout.Popup(label.text, 0, opts);
            return q > 0 ? ids[q - 1] : value;
        }
    }
}
