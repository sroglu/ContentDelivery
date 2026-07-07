using NUnit.Framework;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// The in-bundle name selector that distinguishes a true sub-asset load (<c>main[sub]</c> →
    /// <c>LoadAssetWithSubAssets</c> + pick <c>sub</c>) from a plain asset load (G5). Parsing is the failure-prone
    /// part; the actual sub-asset pick over a real composite asset is exercised manually / in PlayMode.
    /// </summary>
    public sealed class SubAssetAddressTests
    {
        [Test]
        public void Splits_MainAndSubAsset()
        {
            Assert.IsTrue(RemoteBundleAssetSource.TrySplitSubAsset("atlas[hero]", out var main, out var sub));
            Assert.AreEqual("atlas", main);
            Assert.AreEqual("hero", sub);
        }

        [Test]
        public void PlainName_IsNotASubAsset()
        {
            Assert.IsFalse(RemoteBundleAssetSource.TrySplitSubAsset("ui/menu", out var main, out var sub));
            Assert.AreEqual("ui/menu", main, "a plain name passes through unchanged as the main asset");
            Assert.IsNull(sub);
        }

        [Test]
        public void EmptySelector_IsNotASubAsset()
        {
            // "main[]" has no selector → treat as a plain (if odd) name, not a sub-asset request.
            Assert.IsFalse(RemoteBundleAssetSource.TrySplitSubAsset("main[]", out _, out _));
        }

        [Test]
        public void MissingOpenBracket_IsNotASubAsset()
        {
            Assert.IsFalse(RemoteBundleAssetSource.TrySplitSubAsset("weird]", out _, out _));
        }

        [Test]
        public void NullOrEmpty_IsNotASubAsset()
        {
            Assert.IsFalse(RemoteBundleAssetSource.TrySplitSubAsset("", out _, out _));
            Assert.IsFalse(RemoteBundleAssetSource.TrySplitSubAsset(null, out _, out _));
        }
    }
}
