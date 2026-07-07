using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>An <see cref="IAssetSource"/> that also reports canned resident bundles, so the reporter's
    /// "collect bundles from every IBundleMemorySource" path is testable without a real AssetBundle build.</summary>
    internal sealed class FakeBundleSource : IAssetSource, IBundleMemorySource
    {
        public readonly List<BundleMemoryRow> Bundles = new List<BundleMemoryRow>();
        public UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object => UniTask.FromResult<T>(null);
        public void Release(AssetAddress address) { }
        public IReadOnlyList<BundleMemoryRow> SnapshotBundleRows() => Bundles;
    }

    public sealed class MemoryReportTests
    {
        [SetUp]
        public void Reset() => AssetManager.ResetForTests();

        [Test]
        public void Builder_RollsUpCountsRefsSizesAndPerLoader()
        {
            var assets = new[]
            {
                new AssetMemoryRow("a", "TestAsset", 3, "FakeAssetSource",
                    new[] { new LoaderRef("loaderA", 2), new LoaderRef("loaderB", 1) }, -1),
                new AssetMemoryRow("b", "TestAsset", 1, "FakeAssetSource",
                    new[] { new LoaderRef("loaderA", 1) }, -1),
            };
            var bundles = new[]
            {
                new BundleMemoryRow("ui", "h1", 2, 100),
                new BundleMemoryRow("lvl", "h2", 1, 250),
            };

            var report = ContentMemoryReport.From(assets, bundles);

            Assert.AreEqual(2, report.TotalLoadedAssets);
            Assert.AreEqual(2, report.TotalLoadedBundles);
            Assert.AreEqual(4, report.TotalAssetRefCount);   // 3 + 1
            Assert.AreEqual(3, report.TotalBundleRefCount);  // 2 + 1
            Assert.AreEqual(350, report.TotalSizeOnDisk);    // 100 + 250
            Assert.AreEqual(-1, report.TotalRuntimeMemorySize, "no deep pass → runtime size unmeasured");

            // loaderA: 2 (on a) + 1 (on b) = 3; loaderB: 1; ascending by loader id.
            Assert.AreEqual(2, report.ByLoaderTotals.Count);
            Assert.AreEqual("loaderA", report.ByLoaderTotals[0].Loader);
            Assert.AreEqual(3, report.ByLoaderTotals[0].RefCount);
            Assert.AreEqual("loaderB", report.ByLoaderTotals[1].Loader);
            Assert.AreEqual(1, report.ByLoaderTotals[1].RefCount);
        }

        [Test]
        public void Builder_DeepPass_SumsMeasuredRuntimeSizes()
        {
            var assets = new[]
            {
                new AssetMemoryRow("a", "T", 1, "S", new[] { new LoaderRef("g", 1) }, 64),
                new AssetMemoryRow("b", "T", 1, "S", new[] { new LoaderRef("g", 1) }, 128),
            };
            var report = ContentMemoryReport.From(assets, System.Array.Empty<BundleMemoryRow>());
            Assert.AreEqual(192, report.TotalRuntimeMemorySize);
        }

        [Test]
        public void Json_CarriesRollupsAndRows()
        {
            var report = ContentMemoryReport.From(
                new[] { new AssetMemoryRow("addr", "TestAsset", 1, "FakeAssetSource", new[] { new LoaderRef("g", 1) }, -1) },
                new[] { new BundleMemoryRow("ui", "h1", 1, 42) });
            string json = report.ToJson();
            StringAssert.Contains("\"totalSizeOnDisk\":42", json);
            StringAssert.Contains("\"address\":\"addr\"", json);
            StringAssert.Contains("\"hash\":\"h1\"", json);
        }

        [UnityTest]
        public IEnumerator Capture_ReflectsLoadedAssetsBundlesAndPerLoaderSplit() => UniTask.ToCoroutine(async () =>
        {
            var fake = new FakeAssetSource();
            fake.Add("shared");
            AssetManager.RegisterSource(fake);

            var bundleSource = new FakeBundleSource();
            bundleSource.Bundles.Add(new BundleMemoryRow("ui", "h1", 1, 512));
            AssetManager.RegisterSource(bundleSource);

            var a = new AssetLoaderId("loaderA");
            var b = new AssetLoaderId("loaderB");
            await AssetManager.LoadAssetAsync<TestAsset>("shared", a).Task;
            await AssetManager.LoadAssetAsync<TestAsset>("shared", b).Task;

            var report = ContentMemoryReporter.Capture();

            Assert.AreEqual(1, report.TotalLoadedAssets);
            Assert.AreEqual(2, report.TotalAssetRefCount, "two owners each hold one reference");
            Assert.AreEqual(1, report.TotalLoadedBundles, "bundle collected from the IBundleMemorySource");
            Assert.AreEqual(512, report.TotalSizeOnDisk);

            var asset = report.Assets[0];
            Assert.AreEqual("shared", asset.Address);
            Assert.AreEqual("TestAsset", asset.TypeName);
            Assert.AreEqual("FakeAssetSource", asset.Source);
            Assert.AreEqual(-1, asset.RuntimeMemorySize, "shallow capture leaves runtime size unmeasured");

            // Per-owner split is preserved through the snapshot.
            Assert.AreEqual(2, report.ByLoaderTotals.Count);
            int a2 = Find(report.ByLoaderTotals, a.ToString());
            int b2 = Find(report.ByLoaderTotals, b.ToString());
            Assert.AreEqual(1, a2);
            Assert.AreEqual(1, b2);
        });

        private static int Find(IReadOnlyList<LoaderRef> totals, string loader)
        {
            for (int i = 0; i < totals.Count; i++)
                if (totals[i].Loader == loader) return totals[i].RefCount;
            return 0;
        }
    }
}
