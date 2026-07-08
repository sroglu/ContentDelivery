using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>How a group's content reaches the player.</summary>
    public enum DistributionMode
    {
        /// <summary>Bundle ships inside the build (StreamingAssets) — no CDN, no post-launch update.</summary>
        Local = 0,

        /// <summary>Bundle is uploaded to the CDN and pulled at runtime — updatable without an app rebuild.</summary>
        Remote = 1,
    }

    /// <summary>How a group's assets are packed into bundles.</summary>
    public enum BundlePackingMode
    {
        /// <summary>All of the group's assets go into one bundle (fewer files, coarser updates).</summary>
        PackTogether = 0,

        /// <summary>Each asset becomes its own bundle (granular updates, more requests).</summary>
        PackSeparately = 1,
    }

    /// <summary>One addressable asset in a group: the asset and the stable string address callers resolve.</summary>
    [Serializable]
    public sealed class AssetEntry
    {
        [HorizontalGroup(0.5f), HideLabel, Required] public UnityEngine.Object Asset;
        [HorizontalGroup(0.5f), HideLabel, Tooltip("Stable load address (unique within the group).")] public string Address;

        [Tooltip("Free-form tags emitted into the catalog; query them at runtime with Catalog.AssetsWithLabel.")]
        public List<string> Labels = new List<string>();
    }

    /// <summary>
    /// Authoring object for a unit of deliverable content: a set of assets with stable addresses, how they
    /// pack into bundles, their distribution channel, and their load phase. The build pipeline turns groups
    /// into bundles + a JSON catalog; nothing here ships in the player (editor assembly). Odin inspector.
    /// </summary>
    [CreateAssetMenu(menuName = "PFound/Content Delivery/Asset Group", fileName = "AssetGroup")]
    public sealed class AssetGroup : ScriptableObject, IContentAuthoringValidation
    {
        [InfoBox("@AuthoringValidation.Errors(this)", InfoMessageType.Error, "@AuthoringValidation.HasErrors(this)")]
        [InfoBox("@AuthoringValidation.Warnings(this)", InfoMessageType.Warning, "@AuthoringValidation.HasWarnings(this)")]
        [InfoBox("Ready — no authoring issues.", InfoMessageType.Info, "@AuthoringValidation.Ready(this)")]
        [FoldoutGroup("Bundle", expanded: true)]
        [Tooltip("Logical bundle id (also the catalog bundle Name). Defaults to the asset file name when empty.")]
        public string BundleName;

        [FoldoutGroup("Bundle")] public DistributionMode Distribution = DistributionMode.Remote;
        [FoldoutGroup("Bundle")] public BundlePackingMode Packing = BundlePackingMode.PackTogether;

        [FoldoutGroup("Bundle"), DisableIf(nameof(IsLocal))]
        [Tooltip("Phased-preload band for remote content. Local groups load synchronously — ignored there.")]
        public AssetPhase Phase = AssetPhase.Standard;

        [FoldoutGroup("Bundle")]
        [Tooltip("Optional content-pack id. Bundles from groups sharing this pack name ship/update as one unit " +
                 "(queried at runtime via Catalog.GetPackClosure). Empty = not part of a pack.")]
        public string Pack;

        [FoldoutGroup("Bundle")]
        [Tooltip("Dev-only group: dropped from a production build (BuildScope.ExcludeInProd). Built normally otherwise.")]
        public bool ExcludeInProduction;

        [FoldoutGroup("Entries", expanded: true)]
        [ListDrawerSettings(ShowFoldout = false, ListElementLabelName = nameof(AssetEntry.Address))]
        [Tooltip("Each asset + its stable Address (the load key) + optional Labels.")]
        public List<AssetEntry> Entries = new List<AssetEntry>();

        bool IsLocal => Distribution == DistributionMode.Local;

        /// <summary>The effective bundle id: <see cref="BundleName"/> or the asset's name when unset.</summary>
        public string ResolveBundleName() => string.IsNullOrEmpty(BundleName) ? name : BundleName;

        /// <summary>Inspector self-validation: catch the content-authoring mistakes that orphan addresses at runtime.</summary>
        public IEnumerable<AuthoringIssue> Validate()
        {
            if (Entries.Count == 0)
                yield return AuthoringIssue.Warning("No entries — this group builds an empty bundle.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                string where = e.Asset ? e.Asset.name : $"entry {i}";
                if (!e.Asset)
                    yield return AuthoringIssue.Error($"[{where}] has no Asset assigned.");
                if (string.IsNullOrWhiteSpace(e.Address))
                    yield return AuthoringIssue.Error($"[{where}] has an empty Address — it can never be loaded.");
                else if (!seen.Add(e.Address))
                    yield return AuthoringIssue.Error($"Duplicate Address '{e.Address}' — addresses must be unique within a group (later wins, the other orphans).");
            }
        }
    }
}
