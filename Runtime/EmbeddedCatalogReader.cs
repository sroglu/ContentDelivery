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
            // Every miss below logs the PATH it missed at. A silent NotFound is indistinguishable from
            // "this build ships no embedded content", which is what made a wrong embedded root (the
            // AssetBundles/<platform> segment omitted from the Local origin) survive undetected for weeks.
            string dir = ContentPlatform.GetEmbeddedAssetBundlePath(platformFolder);

            string pointerPath = Combine(dir, AssetBundleLayout.EmbeddedCatalogPointerFileName);
            byte[] pointerBytes = await TryReadBytesAsync(pointerPath);
            if (pointerBytes == null)
            {
                Debug.LogError($"[ContentDelivery] Embedded catalog pointer unreadable at '{pointerPath}'.");
                return EmbeddedCatalogResult.NotFound;
            }

            string catalogFileName = Encoding.UTF8.GetString(pointerBytes).Trim();
            if (catalogFileName.Length == 0)
            {
                Debug.LogError($"[ContentDelivery] Embedded catalog pointer at '{pointerPath}' is empty.");
                return EmbeddedCatalogResult.NotFound;
            }

            string catalogPath = Combine(dir, catalogFileName);
            byte[] catalogBytes = await TryReadBytesAsync(catalogPath);
            if (catalogBytes == null)
            {
                Debug.LogError($"[ContentDelivery] Embedded catalog '{catalogFileName}' unreadable at '{catalogPath}' " +
                               "(the pointer names it, so the build staged a pointer without its catalog).");
                return EmbeddedCatalogResult.NotFound;
            }

            // Decoding may fail on a malformed/incomplete embedded catalog (a build defect).
            // Fail-soft: log and return NotFound instead of crashing boot, honoring Constitution §II.
            try
            {
                Catalog catalog = CatalogCodec.Decode(catalogBytes, ParseJson);
                return new EmbeddedCatalogResult(true, catalog, catalogFileName);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ContentDelivery] Failed to decode embedded catalog '{catalogFileName}': {e.Message} — booting without it.");
                return EmbeddedCatalogResult.NotFound;
            }
        }

        private static Catalog ParseJson(byte[] bytes) => CatalogJson.Parse(Encoding.UTF8.GetString(bytes));

        private static string Combine(string dir, string file) =>
            dir.Contains("://") ? AssetBundleLayout.CombineUrl(dir, file) : Path.Combine(dir, file);

        // Reads a StreamingAssets file's bytes, or null if it is absent/unreadable (the fail-soft boundary).
        private static async UniTask<byte[]> TryReadBytesAsync(string path)
        {
            // StreamingAssets is a jar/URL on Android + WebGL — File IO cannot reach it, only UnityWebRequest can.
            if (path.Contains("://"))
            {
                using (var request = UnityWebRequest.Get(path))
                {
                    await request.SendWebRequest().ToUniTask();
                    if (request.result == UnityWebRequest.Result.Success) return request.downloadHandler.data;
                    Debug.LogWarning($"[ContentDelivery] Embedded read failed ({request.error}) for '{path}'.");
                    return null;
                }
            }

            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[ContentDelivery] Embedded read IO error for '{path}': {e.Message}");
                return null;
            }
        }
    }
}
