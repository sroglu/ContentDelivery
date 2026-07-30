using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PFound.ContentDelivery.Tests
{
    public sealed class AssetManagerTests
    {
        [SetUp]
        public void Reset()
        {
            AssetManager.DeferredUnloadFrames = 0; // back to immediate-unload for each test
            AssetManager.ResetForTests();
        }

        [TearDown]
        public void Cleanup() => AssetManager.DeferredUnloadFrames = 0;

        [UnityTest]
        public IEnumerator Load_ResolvesCachesAndRefCounts() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("a");
            AssetManager.RegisterSource(fake);

            var first = await AssetManager.LoadAssetAsync<TestAsset>("a");
            Assert.IsNotNull(first);
            Assert.IsTrue(AssetManager.IsAssetLoaded("a"));
            Assert.AreEqual(1, fake.LoadCalls);

            // Second load is a cache hit: no new source call, ref-count now 2.
            await AssetManager.LoadAssetAsync<TestAsset>("a").Task;
            Assert.AreEqual(1, fake.LoadCalls);

            AssetManager.UnloadAsset("a");
            Assert.IsTrue(AssetManager.IsAssetLoaded("a"), "still one reference outstanding");
            AssetManager.UnloadAsset("a");
            Assert.IsFalse(AssetManager.IsAssetLoaded("a"), "last reference released");
            CollectionAssert.Contains(fake.Released, "a", "source.Release called when ref-count hit zero");
        });

        [UnityTest]
        public IEnumerator Miss_IsNotCachedAndStaysRetryable() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource(); // resolves nothing
            AssetManager.RegisterSource(fake);

            var missing = await AssetManager.LoadAssetAsync<TestAsset>("ghost");
            Assert.IsNull(missing);
            Assert.IsFalse(AssetManager.IsAssetLoaded("ghost"));

            // A miss must not be pinned: a retry actually hits the source again.
            await AssetManager.LoadAssetAsync<TestAsset>("ghost").Task;
            Assert.AreEqual(2, fake.LoadCalls);
        });

        [UnityTest]
        public IEnumerator CleanMiss_IsFailedWithoutError_AndAwaitReturnsNull() => UniTask.ToCoroutine(async () =>
        {
            AssetManager.RegisterSource(new FakeAssetSource()); // resolves nothing

            var handle = AssetManager.LoadAssetAsync<TestAsset>("ghost");
            var result = await handle; // a clean miss returns null, does NOT throw
            Assert.IsNull(result);
            Assert.AreEqual(AssetLoadingStatus.Failed, handle.Status, "a miss is terminal-Failed");
            Assert.IsTrue(handle.IsDone);
        });

        [UnityTest]
        public IEnumerator SourceThrows_IsFailed_AndAwaitRethrows() => UniTask.ToCoroutine(async () =>
        {
            AssetManager.RegisterSource(new ThrowingAssetSource());

            var handle = AssetManager.LoadAssetAsync<TestAsset>("boom");
            await handle.Task; // the bridge completes on the terminal state without throwing
            Assert.AreEqual(AssetLoadingStatus.Failed, handle.Status);

            bool rethrew = false;
            try { await handle; } // awaiting the value rethrows the captured source exception
            catch (System.InvalidOperationException) { rethrew = true; }
            Assert.IsTrue(rethrew, "a pipeline error surfaces when the handle is awaited for its value");
        });

        [UnityTest]
        public IEnumerator PerLoader_RefCounts_AreIndependent() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("shared");
            AssetManager.RegisterSource(fake);

            var a = new AssetLoaderId("loaderA");
            var b = new AssetLoaderId("loaderB");

            await AssetManager.LoadAssetAsync<TestAsset>("shared", a).Task;
            await AssetManager.LoadAssetAsync<TestAsset>("shared", b).Task;
            Assert.AreEqual(1, fake.LoadCalls, "the second loader is a cache hit on the same address");
            Assert.AreEqual(1, AssetManager.RefCountFor("shared", a));
            Assert.AreEqual(1, AssetManager.RefCountFor("shared", b));

            // Loader A releasing must not free the asset while B still holds it.
            AssetManager.UnloadAsset("shared", a);
            Assert.AreEqual(0, AssetManager.RefCountFor("shared", a));
            Assert.IsTrue(AssetManager.IsAssetLoaded("shared"), "loaderB's reference keeps it resident");
            CollectionAssert.DoesNotContain(fake.Released, "shared");

            AssetManager.UnloadAsset("shared", b);
            Assert.IsFalse(AssetManager.IsAssetLoaded("shared"), "last owner released → freed");
            CollectionAssert.Contains(fake.Released, "shared");
        });

        [UnityTest]
        public IEnumerator Instantiate_HoldsRef_DestroyReleases() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("prefab");
            AssetManager.RegisterSource(fake);

            var instance = await AssetManager.InstantiateAsync<TestAsset>("prefab");
            Assert.IsNotNull(instance);
            Assert.IsTrue(AssetManager.IsAssetLoaded("prefab"), "instance holds a reference on its source asset");

            AssetManager.Destroy(instance);
            Assert.IsFalse(AssetManager.IsAssetLoaded("prefab"), "destroying the instance releases the reference");
            CollectionAssert.Contains(fake.Released, "prefab");
        });

        [UnityTest]
        public IEnumerator TotalRefCount_SumsAcrossOwners() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("x");
            AssetManager.RegisterSource(fake);

            Assert.AreEqual(0, AssetManager.RefCount("x"), "unresident address reports zero");
            await AssetManager.LoadAssetAsync<TestAsset>("x", new AssetLoaderId("a")).Task;
            await AssetManager.LoadAssetAsync<TestAsset>("x", new AssetLoaderId("b")).Task;
            Assert.AreEqual(2, AssetManager.RefCount("x"), "total counts both owners");
        });

        [UnityTest]
        public IEnumerator DeferredUnload_KeepsResidentThenReleasesOnPump() => UniTask.ToCoroutine(async () =>
        {
            AssetManager.DeferredUnloadFrames = 2;
            var fake = new FakeAssetSource();
            fake.Add("d");
            AssetManager.RegisterSource(fake);

            await AssetManager.LoadAssetAsync<TestAsset>("d").Task;
            int frame = Time.frameCount;
            AssetManager.UnloadAsset("d");

            Assert.IsTrue(AssetManager.IsAssetLoaded("d"), "kept resident during the grace window");
            CollectionAssert.DoesNotContain(fake.Released, "d", "source not released yet");

            AssetManager.PumpDeferredUnloads(frame + 1); // still inside the window
            Assert.IsTrue(AssetManager.IsAssetLoaded("d"));

            AssetManager.PumpDeferredUnloads(frame + 2); // window elapsed
            Assert.IsFalse(AssetManager.IsAssetLoaded("d"), "released once the window elapsed");
            CollectionAssert.Contains(fake.Released, "d");
        });

        [UnityTest]
        public IEnumerator DeferredUnload_ReloadWithinWindowRevives() => UniTask.ToCoroutine(async () =>
        {
            AssetManager.DeferredUnloadFrames = 3;
            var fake = new FakeAssetSource();
            fake.Add("r");
            AssetManager.RegisterSource(fake);

            await AssetManager.LoadAssetAsync<TestAsset>("r").Task;
            int frame = Time.frameCount;
            AssetManager.UnloadAsset("r");            // queued for deferred release

            await AssetManager.LoadAssetAsync<TestAsset>("r").Task; // re-load within the window revives it
            Assert.AreEqual(1, fake.LoadCalls, "revived from cache — no reload");

            AssetManager.PumpDeferredUnloads(frame + 10); // well past the window
            Assert.IsTrue(AssetManager.IsAssetLoaded("r"), "revived entry is not released");
            CollectionAssert.DoesNotContain(fake.Released, "r");
        });

        [UnityTest]
        public IEnumerator LowMemory_FlushesDeferredResidency() => UniTask.ToCoroutine(async () =>
        {
            AssetManager.DeferredUnloadFrames = 100; // long window — only a flush should release it
            var fake = new FakeAssetSource();
            fake.Add("m");
            AssetManager.RegisterSource(fake);

            await AssetManager.LoadAssetAsync<TestAsset>("m").Task;
            AssetManager.UnloadAsset("m");
            Assert.IsTrue(AssetManager.IsAssetLoaded("m"), "still within the long grace window");

            AssetManager.HandleLowMemory(); // releases non-pinned residency immediately
            Assert.IsFalse(AssetManager.IsAssetLoaded("m"), "deferred residency flushed under memory pressure");
            CollectionAssert.Contains(fake.Released, "m");
        });
    }
}
