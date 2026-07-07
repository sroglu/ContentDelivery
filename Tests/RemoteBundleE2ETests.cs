using System;
using System.Collections;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// Full remote path proven against a real AssetBundle built in setup: catalog resolve → provision over
    /// file:// → hash-verify → cache → AssetBundle load → resolve asset by address. The CDN is stood in by a
    /// local origin directory (content-addressed file names), exactly as <c>DirectoryUploader</c> would publish.
    /// </summary>
    public sealed class RemoteBundleE2ETests
    {
        private const string AssetFolder = "Assets/__pf_cd_e2e";
        private const string Address = "mat/red";

        private string _root;
        private string _origin;
        private string _cache;
        private Catalog _catalog;

        [OneTimeSetUp]
        public void BuildBundle()
        {
            _root = Path.Combine(Application.temporaryCachePath, "pf_cd_e2e_" + Guid.NewGuid().ToString("N"));
            _origin = Path.Combine(_root, "origin");
            _cache = Path.Combine(_root, "cache");
            string staging = Path.Combine(_root, "staging");
            Directory.CreateDirectory(_origin);
            Directory.CreateDirectory(staging);

            if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets", "__pf_cd_e2e");
            var material = new Material(Shader.Find("Unlit/Color")) { name = "Red" };
            string matPath = AssetFolder + "/Red.mat";
            AssetDatabase.CreateAsset(material, matPath);
            AssetDatabase.SaveAssets();

            var build = new AssetBundleBuild
            {
                assetBundleName = "e2e_bundle",
                assetNames = new[] { matPath },
                addressableNames = new[] { Address },
            };
            var manifest = BuildPipeline.BuildAssetBundles(
                staging, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);
            Assert.IsNotNull(manifest, "bundle build failed");

            string hash = ContentHash.ComputeFile(Path.Combine(staging, "e2e_bundle"));
            File.Copy(Path.Combine(staging, "e2e_bundle"), Path.Combine(_origin, hash), true);

            _catalog = new Catalog(
                new[] { new CatalogBundle { Name = "e2e_bundle", Hash = hash, Dependencies = Array.Empty<string>() } },
                new[] { new CatalogAsset { Address = Address, Bundle = "e2e_bundle", AssetName = Address, Phase = 0 } });
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            AssetDatabase.DeleteAsset(AssetFolder);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [UnityTest]
        public IEnumerator LoadsRealBundleByAddress_AndMissesUnknown() => UniTask.ToCoroutine(async () =>
        {
            // Bundle hash computed with the SHA-256 ContentHash helper above → pin the source to SHA-256.
            var source = new RemoteBundleAssetSource(
                _catalog, new UnityWebRequestTransport(), _cache, new Uri(_origin).AbsoluteUri, hasher: new Sha256ContentHasher());

            var loaded = await source.LoadAsync<Material>(Address);
            Assert.IsNotNull(loaded, "asset should load from the provisioned bundle");
            Assert.AreEqual("Red", loaded.name);

            var unknown = await source.LoadAsync<Material>("does/not/exist");
            Assert.IsNull(unknown, "address absent from the catalog must miss so a fallback source can try");

            source.Release(Address); // unload the bundle so a re-run can load the file again
        });
    }
}
