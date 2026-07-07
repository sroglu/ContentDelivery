using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine.TestTools;

namespace PFound.ContentDelivery.Tests
{
    public sealed class AssetLoaderTests
    {
        [SetUp]
        public void Reset() => AssetManager.ResetForTests();

        [UnityTest]
        public IEnumerator Dispose_ReleasesOwnedAddresses() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("a");
            fake.Add("b");
            AssetManager.RegisterSource(fake);

            var loader = new AssetLoader(new FixedString64Bytes("owner"));
            await loader.LoadAssetAsync<TestAsset>("a").Task;
            await loader.LoadAssetAsync<TestAsset>("b").Task;
            Assert.IsTrue(AssetManager.IsAssetLoaded("a"));
            Assert.IsTrue(AssetManager.IsAssetLoaded("b"));

            loader.Dispose();
            Assert.IsFalse(AssetManager.IsAssetLoaded("a"));
            Assert.IsFalse(AssetManager.IsAssetLoaded("b"));
        });

        [UnityTest]
        public IEnumerator UnloadAsset_ReleasesSingleAddress() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("a");
            AssetManager.RegisterSource(fake);

            var loader = new AssetLoader(new FixedString64Bytes("owner"));
            await loader.LoadAssetAsync<TestAsset>("a").Task;
            loader.UnloadAsset("a");
            Assert.IsFalse(AssetManager.IsAssetLoaded("a"));
            loader.Dispose(); // double-release must not throw
        });

        [UnityTest]
        public IEnumerator Dispose_DestroysInstancesAndReleasesTheirRefs() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("prefab");
            AssetManager.RegisterSource(fake);

            var loader = new AssetLoader(new FixedString64Bytes("owner"));
            var instance = await loader.InstantiateAsync<TestAsset>("prefab");
            Assert.IsNotNull(instance);
            Assert.IsTrue(AssetManager.IsAssetLoaded("prefab"));

            loader.Dispose();
            Assert.IsFalse(AssetManager.IsAssetLoaded("prefab"), "instance ref released on dispose");
            CollectionAssert.Contains(fake.Released, "prefab");
        });
    }
}
