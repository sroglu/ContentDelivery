using System.Collections.Generic;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Assembles the runtime catalog JSON from build outputs. Kept free of Unity's build API so the
    /// address→bundle→hash→deps mapping is verifiable on its own (the heavy <c>BuildAssetBundles</c> step
    /// lives in <see cref="BundleBuildPipeline"/>). Emits through <see cref="CatalogJson.ToJson"/>, the same
    /// format the runtime reader parses.
    /// </summary>
    public static class CatalogBuilder
    {
        /// <summary>One resolvable address and the bundle that owns it.</summary>
        public struct AddressRecord
        {
            public string Address;
            public string Bundle;
            public int Phase;
            public string[] Labels;
        }

        /// <summary>A built bundle: content hash + its direct dependency bundle names (closure is walked at runtime).</summary>
        public struct BuiltBundle
        {
            public string Name;
            public string Hash;             // hash of the stored (compressed) object
            public string UncompressedHash; // hash of the raw bundle (== Hash when uncompressed)
            public string[] DirectDependencies;
            public bool Local;   // DistributionMode.Local → ships in StreamingAssets rather than the CDN
            public BundleCompression Compression;
        }

        /// <summary>A named pack: the bundles that ship/update as one unit (their deps are resolved at runtime).</summary>
        public struct PackRecord
        {
            public string Name;
            public string[] Bundles;
        }

        public static string ToJson(
            IReadOnlyList<AddressRecord> addresses, IReadOnlyList<BuiltBundle> bundles,
            IReadOnlyList<PackRecord> packs = null, string version = null)
        {
            var catalogBundles = new List<CatalogBundle>(bundles.Count);
            for (int i = 0; i < bundles.Count; i++)
            {
                var b = bundles[i];
                catalogBundles.Add(new CatalogBundle
                {
                    Name = b.Name,
                    Hash = b.Hash,
                    UncompressedHash = string.IsNullOrEmpty(b.UncompressedHash) ? b.Hash : b.UncompressedHash,
                    Dependencies = b.DirectDependencies ?? System.Array.Empty<string>(),
                    Local = b.Local,
                    Compression = b.Compression,
                });
            }

            var catalogAssets = new List<CatalogAsset>(addresses.Count);
            for (int i = 0; i < addresses.Count; i++)
            {
                var a = addresses[i];
                catalogAssets.Add(new CatalogAsset
                {
                    Address = a.Address,
                    Bundle = a.Bundle,
                    // We build bundles with the address as the addressable (in-bundle) name, so they coincide.
                    AssetName = a.Address,
                    Phase = a.Phase,
                    Labels = a.Labels ?? System.Array.Empty<string>(),
                });
            }

            List<CatalogPack> catalogPacks = null;
            if (packs != null && packs.Count > 0)
            {
                catalogPacks = new List<CatalogPack>(packs.Count);
                for (int i = 0; i < packs.Count; i++)
                    catalogPacks.Add(new CatalogPack { Name = packs[i].Name, Bundles = packs[i].Bundles ?? System.Array.Empty<string>() });
            }

            return CatalogJson.ToJson(catalogBundles, catalogAssets, catalogPacks, version);
        }
    }
}
