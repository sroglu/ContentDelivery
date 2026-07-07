using System;
using System.Collections.Generic;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>Which <see cref="AssetGroup"/>s a content build includes — a pre-build filter over the gathered groups.</summary>
    public enum BuildScope
    {
        /// <summary>Every group in the project.</summary>
        AllGroups,

        /// <summary>Only the explicitly-provided (e.g. Project-window-selected) groups.</summary>
        OnlySelected,

        /// <summary>Every group except those flagged <see cref="AssetGroup.ExcludeInProduction"/> (a production build).</summary>
        ExcludeInProd,
    }

    /// <summary>
    /// Applies a <see cref="BuildScope"/> to the gathered groups before they reach the build pipeline — the scope is
    /// a plain pre-build set selection (it does not touch SBP). Order is preserved; nulls are dropped.
    /// </summary>
    public static class BuildScopeFilter
    {
        public static List<AssetGroup> Apply(
            IReadOnlyList<AssetGroup> allGroups, BuildScope scope, ICollection<AssetGroup> selected = null)
        {
            if (allGroups == null) throw new ArgumentNullException(nameof(allGroups));

            var result = new List<AssetGroup>(allGroups.Count);
            for (int i = 0; i < allGroups.Count; i++)
            {
                var group = allGroups[i];
                if (group == null) continue;
                switch (scope)
                {
                    case BuildScope.OnlySelected:
                        if (selected != null && selected.Contains(group)) result.Add(group);
                        break;
                    case BuildScope.ExcludeInProd:
                        if (!group.ExcludeInProduction) result.Add(group);
                        break;
                    default: // AllGroups
                        result.Add(group);
                        break;
                }
            }
            return result;
        }
    }
}
