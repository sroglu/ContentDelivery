using System;
using NUnit.Framework;

namespace PFound.ContentDelivery.Tests
{
    public sealed class AddressTests
    {
        [Test]
        public void AssetAddress_ImplicitFromString_AndEquality()
        {
            AssetAddress a = "ui/menu";
            AssetAddress b = new AssetAddress("ui/menu");
            Assert.IsTrue(a.IsValid);
            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a, new AssetAddress("ui/other"));
        }

        [Test]
        public void AssetAddress_Default_IsInvalid()
        {
            AssetAddress empty = default;
            Assert.IsFalse(empty.IsValid);
            Assert.AreEqual("<invalid>", empty.ToString());
        }

        [Test]
        public void UnmanagedAssetAddress_RoundTripsToManaged()
        {
            var u = new UnmanagedAssetAddress("levels/forest");
            Assert.IsTrue(u.IsValid);
            Assert.AreEqual("levels/forest", u.ToString());
            Assert.AreEqual(new AssetAddress("levels/forest"), u.ToManaged());
            Assert.AreEqual(new AssetAddress("levels/forest"), (AssetAddress)u);
        }

        [Test]
        public void UnmanagedAssetAddress_Equality()
        {
            var a = new UnmanagedAssetAddress("a/b");
            UnmanagedAssetAddress b = new UnmanagedAssetAddress("a/b");
            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a, new UnmanagedAssetAddress("a/c"));
        }

        [Test]
        public void UnmanagedAssetAddress_RejectsOverlongAddress()
        {
            // FixedString128Bytes holds <128 UTF-8 bytes; a longer address must throw, not silently truncate.
            string tooLong = new string('x', 200);
            Assert.Throws<ArgumentException>(() => { var _ = new UnmanagedAssetAddress(tooLong); });
        }
    }
}
