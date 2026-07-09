using System;
using System.Collections.Generic;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Parses the JSON catalog (address → bundle → hash → dependencies, plus per-asset phase) into a
    /// pure-C# <see cref="Catalog"/>. The on-disk shape is owned here; <c>Core</c> stays Unity- and
    /// format-free. Shape:
    /// <code>
    /// { "bundles":[{"name","hash","dependencies":[]}],
    ///   "assets":[{"address","bundle","assetName","phase","labels":[]}],
    ///   "packs":[{"name","bundles":[]}] }
    /// </code>
    /// </summary>
    public static class CatalogJson
    {
        [Serializable]
        private struct BundleDto { public string name; public string hash; public string uncompressedHash; public string[] dependencies; public bool local; public int compression; }

        [Serializable]
        private struct AssetDto { public string address; public string bundle; public string assetName; public int phase; public string[] labels; }

        [Serializable]
        private struct PackDto { public string name; public string[] bundles; }

        [Serializable]
        private struct CatalogDto { public string version; public string buildMode; public BundleDto[] bundles; public AssetDto[] assets; public PackDto[] packs; }

        /// <summary>Builds a <see cref="Catalog"/> from a JSON document.</summary>
        public static Catalog Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Catalog JSON is empty.", nameof(json));

            var dto = JsonUtility.FromJson<CatalogDto>(json);
            var bundleDtos = dto.bundles ?? Array.Empty<BundleDto>();
            var assetDtos = dto.assets ?? Array.Empty<AssetDto>();
            var packDtos = dto.packs ?? Array.Empty<PackDto>();

            var bundles = new CatalogBundle[bundleDtos.Length];
            for (int i = 0; i < bundleDtos.Length; i++)
            {
                var b = bundleDtos[i];
                bundles[i] = new CatalogBundle
                {
                    Name = b.name,
                    Hash = b.hash,
                    // Fall back to the stored hash when uncompressed-hash is absent (uncompressed bundles coincide).
                    UncompressedHash = string.IsNullOrEmpty(b.uncompressedHash) ? b.hash : b.uncompressedHash,
                    Dependencies = b.dependencies ?? Array.Empty<string>(),
                    Local = b.local,
                    Compression = (BundleCompression)b.compression,
                };
            }

            var assets = new CatalogAsset[assetDtos.Length];
            for (int i = 0; i < assetDtos.Length; i++)
            {
                var a = assetDtos[i];
                assets[i] = new CatalogAsset
                {
                    Address = a.address,
                    Bundle = a.bundle,
                    // Fall back to the address as the in-bundle name when an explicit alias is omitted.
                    AssetName = string.IsNullOrEmpty(a.assetName) ? a.address : a.assetName,
                    Phase = a.phase,
                    Labels = a.labels ?? Array.Empty<string>(),
                };
            }

            var packs = new CatalogPack[packDtos.Length];
            for (int i = 0; i < packDtos.Length; i++)
            {
                var p = packDtos[i];
                packs[i] = new CatalogPack
                {
                    Name = p.name,
                    Bundles = p.bundles ?? Array.Empty<string>(),
                };
            }

            // buildMode is optional — absent on catalogs built before the field existed (⇒ null).
            string buildMode = string.IsNullOrEmpty(dto.buildMode) ? null : dto.buildMode;
            return new Catalog(bundles, assets, dto.version, packs, buildMode);
        }

        /// <summary>
        /// Serializes catalog models to the JSON document <see cref="Parse"/> reads — the editor build
        /// pipeline emits with this so write and read can never drift. Round-trips: every address/bundle
        /// written here resolves after parsing.
        /// </summary>
        public static string ToJson(
            IReadOnlyList<CatalogBundle> bundles, IReadOnlyList<CatalogAsset> assets,
            IReadOnlyList<CatalogPack> packs = null, string version = null, string buildMode = null, bool prettyPrint = true)
        {
            if (bundles == null) throw new ArgumentNullException(nameof(bundles));
            if (assets == null) throw new ArgumentNullException(nameof(assets));

            var dto = new CatalogDto
            {
                version = version,
                buildMode = buildMode,
                bundles = new BundleDto[bundles.Count],
                assets = new AssetDto[assets.Count],
                packs = new PackDto[packs?.Count ?? 0],
            };
            for (int i = 0; i < bundles.Count; i++)
            {
                var b = bundles[i];
                dto.bundles[i] = new BundleDto
                {
                    name = b.Name,
                    hash = b.Hash,
                    uncompressedHash = b.UncompressedHash,
                    dependencies = b.Dependencies ?? Array.Empty<string>(),
                    local = b.Local,
                    compression = (int)b.Compression,
                };
            }
            for (int i = 0; i < assets.Count; i++)
            {
                var a = assets[i];
                dto.assets[i] = new AssetDto
                {
                    address = a.Address,
                    bundle = a.Bundle,
                    assetName = a.AssetName,
                    phase = a.Phase,
                    labels = a.Labels ?? Array.Empty<string>(),
                };
            }
            for (int i = 0; i < (packs?.Count ?? 0); i++)
            {
                var p = packs[i];
                dto.packs[i] = new PackDto
                {
                    name = p.Name,
                    bundles = p.Bundles ?? Array.Empty<string>(),
                };
            }
            return JsonUtility.ToJson(dto, prettyPrint);
        }
    }
}
