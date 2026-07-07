using System;
using System.Collections.Generic;
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
        public UnityEngine.Object Asset;
        public string Address;

        [Tooltip("Free-form tags emitted into the catalog; query them at runtime with Catalog.AssetsWithLabel.")]
        public List<string> Labels = new List<string>();
    }

    /// <summary>
    /// Authoring object for a unit of deliverable content: a set of assets with stable addresses, how they
    /// pack into bundles, their distribution channel, and their load phase. The build pipeline turns groups
    /// into bundles + a JSON catalog; nothing here ships in the player (editor assembly).
    /// </summary>
    [CreateAssetMenu(menuName = "PFound/Content Delivery/Asset Group", fileName = "AssetGroup")]
    public sealed class AssetGroup : ScriptableObject
    {
        [Tooltip("Logical bundle id (also the catalog bundle Name). Defaults to the asset file name when empty.")]
        public string BundleName;

        public DistributionMode Distribution = DistributionMode.Remote;
        public BundlePackingMode Packing = BundlePackingMode.PackTogether;
        public AssetPhase Phase = AssetPhase.Standard;

        [Tooltip("Optional content-pack id. Bundles from groups sharing this pack name ship/update as one unit " +
                 "(queried at runtime via Catalog.GetPackClosure). Empty = not part of a pack.")]
        public string Pack;

        [Tooltip("Dev-only group: dropped from a production build (BuildScope.ExcludeInProd). Built normally otherwise.")]
        public bool ExcludeInProduction;

        public List<AssetEntry> Entries = new List<AssetEntry>();

        /// <summary>The effective bundle id: <see cref="BundleName"/> or the asset's name when unset.</summary>
        public string ResolveBundleName() => string.IsNullOrEmpty(BundleName) ? name : BundleName;
    }
}
