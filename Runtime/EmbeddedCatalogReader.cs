using System;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>The result of reading the build-embedded catalog.</summary>
    public readonly struct EmbeddedCatalogResult
    {
        public readonly bool Found;
        public readonly Catalog Catalog;
        /// <summary>The embedded catalog's file name (from the pointer) — used to decide embedded-matches-required.</summary>
        public readonly string FileName;

        public EmbeddedCatalogResult(bool found, Catalog catalog, string fileName)
        {
            Found = found;
            Catalog = catalog;
            FileName = fileName;
        }

        public static readonly EmbeddedCatalogResult NotFound = new EmbeddedCatalogResult(false, null, null);
    }

    /// <summary>
    /// Reads the catalog shipped inside the build. Two hops: the small pointer file
    /// (<see cref="AssetBundleLayout.EmbeddedCatalogPointerFileName"/> in
    /// <c>StreamingAssets/AssetBundles/&lt;platform&gt;/</c>) names the catalog
    /// file, then that catalog's bytes are read and decoded via <see cref="CatalogCodec"/> (PFound binary/JSON,
    /// optionally LZMA — see the catalog-format decision). Platform-safe: on platforms whose StreamingAssets is a
    /// jar/URL (Android) it fetches with <see cref="UnityWebRequest"/>; elsewhere it uses <see cref="File"/>.
    /// Fail-soft — an absent or unreadable pointer/catalog yields <see cref="EmbeddedCatalogResult.NotFound"/>
    /// rather than throwing, so a purely-remote app boots normally.
    /// </summary>
    public static class EmbeddedCatalogReader
    {
        public static async UniTask<EmbeddedCatalogResult> TryReadEmbeddedCatalogAsync(string platformFolder = null)
        {
            string dir = ContentPlatform.GetEmbeddedAssetBundlePath(platformFolder);

            byte[] pointerBytes = await TryReadBytesAsync(Combine(dir, AssetBundleLayout.EmbeddedCatalogPointerFileName));
            if (pointerBytes == null) return EmbeddedCatalogResult.NotFound;

            string catalogFileName = Encoding.UTF8.GetString(pointerBytes).Trim();
            if (catalogFileName.Length == 0) return EmbeddedCatalogResult.NotFound;

            byte[] catalogBytes = await TryReadBytesAsync(Combine(dir, catalogFileName));
            if (catalogBytes == null) return EmbeddedCatalogResult.NotFound;

            // Decoding is our own code; a malformed embedded catalog is a build defect, so let it surface (fail-fast)
            // rather than silently booting an app with no content.
            Catalog catalog = CatalogCodec.Decode(catalogBytes, ParseJson);
            return new EmbeddedCatalogResult(true, catalog, catalogFileName);
        }

        private static Catalog ParseJson(byte[] bytes) => CatalogJson.Parse(Encoding.UTF8.GetString(bytes));

        private static string Combine(string dir, string file) =>
            dir.Contains("://") ? AssetBundleLayout.CombineUrl(dir, file) : Path.Combine(dir, file);

        // Reads a StreamingAssets file's bytes, or null if it is absent/unreadable (the fail-soft boundary).
        private static async UniTask<byte[]> TryReadBytesAsync(string path)
        {
            if (path.Contains("://"))
            {
                using (var request = UnityWebRequest.Get(path))
                {
                    await request.SendWebRequest().ToUniTask();
                    return request.result == UnityWebRequest.Result.Success ? request.downloadHandler.data : null;
                }
            }

            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
