using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PFound.ContentDelivery.Editor;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// The editor fast-path source: addresses resolve straight to live project assets (no bundle build / download /
    /// cache), the address→path map is built from the authoring <see cref="AssetGroup"/>, and toggling the source
    /// off lets the load chain fall through to the bundle path. The async live-load + sub-asset pick over a real
    /// composite asset are exercised here / manually, same as the sibling bundle suites.
    /// </summary>
    public sealed class EditorFastPathTests
    {
        private const string AssetFolder = "Assets/__pf_cd_fastpath";
        private const string Address = "fastpath/mat";
        private string _matPath;
        private AssetGroup _group;
        private Material _stale;

        [OneTimeSetUp]
        public void CreateProjectAssets()
        {
            if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets", "__pf_cd_fastpath");

            var material = new Material(Shader.Find("Unlit/Color")) { name = "Red" };
            _matPath = AssetFolder + "/Red.mat";
            AssetDatabase.CreateAsset(material, _matPath);

            _group = ScriptableObject.CreateInstance<AssetGroup>();
            _group.BundleName = "fastpath_bundle";
            _group.Entries = new List<AssetEntry> { new AssetEntry { Asset = material, Address = Address } };
            AssetDatabase.CreateAsset(_group, AssetFolder + "/Group.asset");
            AssetDatabase.SaveAssets();

            // An in-memory stand-in for the "built bundle" path, so a fall-through resolves to a DIFFERENT asset.
            _stale = new Material(Shader.Find("Unlit/Color")) { name = "Stale" };
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            AssetDatabase.DeleteAsset(AssetFolder);
            if (_stale != null) Object.DestroyImmediate(_stale);
        }

        [SetUp]
        public void Reset() => AssetManager.ResetForTests();

        [Test]
        public void Build_MapsAddressToProjectAssetPath()
        {
            var map = EditorAddressMap.Build(new[] { _group });
            Assert.IsTrue(map.TryGetValue(Address, out var path));
            Assert.AreEqual(_matPath, path, "address maps to the live project asset path");
        }

        [UnityTest]
        public IEnumerator Resolve_ReturnsLiveProjectAsset_WithoutBundles() => UniTask.ToCoroutine(async () =>
        {
            var source = new EditorAssetSource(EditorAddressMap.Build(new[] { _group }));

            var loaded = await source.LoadAsync<Material>(Address);
            Assert.IsNotNull(loaded, "address resolves directly to the project asset");
            Assert.AreEqual("Red", loaded.name);
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Material>(_matPath), loaded, "it is the live project asset, not a copy");

            var unknown = await source.LoadAsync<Material>("not/authored");
            Assert.IsNull(unknown, "an address absent from the map misses so the chain can fall through");
        });

        [UnityTest]
        public IEnumerator Toggle_FastPathWins_DisabledFallsThroughToBundlePath() => UniTask.ToCoroutine(async () =>
        {
            // Stand-in for the real bundle source: resolves the same address to the "stale" asset.
            var bundlePath = new FakeAssetSource();
            bundlePath.Map[Address] = _stale;
            AssetManager.RegisterSource(bundlePath);

            // Fast-path ON: registered ahead of the bundle source → project truth wins.
            var fastPath = new EditorAssetSource(EditorAddressMap.Build(new[] { _group }));
            AssetManager.RegisterSource(fastPath);

            var hot = await AssetManager.LoadAssetAsync<Material>(Address);
            Assert.AreEqual("Red", hot.name, "fast-path serves the live project asset");

            // Toggle OFF: drop the fast-path source (and the cached entry) → the chain falls to the bundle path.
            AssetManager.UnregisterSource(fastPath);
            AssetManager.UnloadAsset(Address);

            var cold = await AssetManager.LoadAssetAsync<Material>(Address);
            Assert.AreEqual("Stale", cold.name, "with the fast-path off, the bundle path resolves the address");
        });

        [Test]
        public void Enabled_Persists_AndRestores()
        {
            bool original = EditorFastPathMode.Enabled;
            try
            {
                EditorFastPathMode.Enabled = false;
                Assert.IsFalse(EditorFastPathMode.Enabled);
                EditorFastPathMode.Enabled = true;
                Assert.IsTrue(EditorFastPathMode.Enabled);
            }
            finally { EditorFastPathMode.Enabled = original; }
        }
    }
}
