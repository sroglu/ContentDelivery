using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
        public List<ContentSet> Sets = new List<ContentSet>();
        public List<AssetGroup> AlwaysIncluded = new List<AssetGroup>();
        public CatalogEditorConfig Config;   // the "how": platform / env / offline / Mode

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

        public IEnumerable<AuthoringIssue> Validate() { yield break; } // filled in Task 3
    }
}
