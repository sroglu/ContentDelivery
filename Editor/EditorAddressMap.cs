using System;
using System.Collections.Generic;
using UnityEditor;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Builds the address → project-asset-path map the <see cref="EditorAssetSource"/> resolves against, from the
    /// authoring <see cref="AssetGroup"/>s — the same source of truth the bundle build reads. Each
    /// <see cref="AssetEntry"/>'s <see cref="AssetEntry.Address"/> maps to its asset's project path (sub-asset
    /// selectors like "main[sub]" are kept on the address and split at resolve time, so the path is just the file).
    /// </summary>
    public static class EditorAddressMap
    {
        /// <summary>Builds the map from every <see cref="AssetGroup"/> in the project.</summary>
        public static Dictionary<string, string> Build() => Build(ContentDeliveryMenu.LoadAllGroups());

        /// <summary>Builds the map from the given groups (the form tests drive with a temp group).</summary>
        public static Dictionary<string, string> Build(IEnumerable<AssetGroup> groups)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups)
                foreach (var entry in group.Entries)
                    map[entry.Address] = AssetDatabase.GetAssetPath(entry.Asset);
            return map;
        }
    }
}
