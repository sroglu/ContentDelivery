using System.Collections.Generic;
using NUnit.Framework;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Editor;

namespace PFound.ContentDelivery.Tests
{
    public sealed class CatalogTests
    {
        [Test]
        public void CatalogJson_ParsesBundlesAssetsAndAliasFallback()
        {
            string json = @"{
                ""bundles"":[{""name"":""ui"",""hash"":""h_ui"",""dependencies"":[""shared""]},
                             {""name"":""shared"",""hash"":""h_shared"",""dependencies"":[]}],
                ""assets"":[{""address"":""ui/menu"",""bundle"":""ui"",""assetName"":""MainMenu"",""phase"":0,""labels"":[""ui"",""boot""]},
                            {""address"":""ui/logo"",""bundle"":""ui"",""assetName"":"""",""phase"":100}]
            }";
            var catalog = CatalogJson.Parse(json);

            Assert.IsTrue(catalog.TryResolveAsset("ui/menu", out var menu));
            Assert.AreEqual("ui", menu.Bundle);
            Assert.AreEqual("MainMenu", menu.AssetName);
            Assert.AreEqual(0, menu.Phase);
            CollectionAssert.AreEqual(new[] { "ui", "boot" }, menu.Labels, "labels parsed");

            Assert.IsTrue(catalog.TryResolveAsset("ui/logo", out var logo));
            Assert.AreEqual("ui/logo", logo.AssetName, "empty assetName should fall back to the address");
            Assert.AreEqual(0, logo.Labels.Length, "absent labels parse as empty, not null");

            // A label query spans the catalog and returns only tagged assets.
            var tagged = new List<string>();
            foreach (var a in catalog.AssetsWithLabel("ui")) tagged.Add(a.Address);
            CollectionAssert.AreEqual(new[] { "ui/menu" }, tagged, "only the ui-labelled asset matches");

            Assert.IsFalse(catalog.TryResolveAsset("nope", out _));
        }

        [Test]
        public void Catalog_GetBundleClosure_IsDependenciesFirstAndDeduped()
        {
            string json = @"{
                ""bundles"":[{""name"":""a"",""hash"":""ha"",""dependencies"":[""b"",""c""]},
                             {""name"":""b"",""hash"":""hb"",""dependencies"":[""c""]},
                             {""name"":""c"",""hash"":""hc"",""dependencies"":[]}],
                ""assets"":[]
            }";
            var catalog = CatalogJson.Parse(json);

            var closure = catalog.GetBundleClosure("a");
            var order = new List<string>();
            foreach (var b in closure) order.Add(b.Name);

            Assert.AreEqual(3, order.Count);
            Assert.Less(order.IndexOf("c"), order.IndexOf("b"));
            Assert.Less(order.IndexOf("b"), order.IndexOf("a"));
            Assert.AreEqual("a", order[order.Count - 1]);
        }

        [Test]
        public void CatalogJson_RoundTripsVersionPointer()
        {
            var bundles = new System.Collections.Generic.List<CatalogBundle>
            {
                new CatalogBundle { Name = "ui", Hash = "h_ui" },
            };
            var assets = new System.Collections.Generic.List<CatalogAsset>();

            var versioned = CatalogJson.Parse(CatalogJson.ToJson(bundles, assets, version: "v-123"));
            Assert.AreEqual("v-123", versioned.Version);

            // Absent version parses as null/empty, not a crash — the pointer is optional.
            var unversioned = CatalogJson.Parse(@"{""bundles"":[],""assets"":[]}");
            Assert.IsTrue(string.IsNullOrEmpty(unversioned.Version));
        }

        [Test]
        public void CatalogBuilder_WriterRoundTripsThroughReader()
        {
            var addresses = new List<CatalogBuilder.AddressRecord>
            {
                new CatalogBuilder.AddressRecord { Address = "ui/menu", Bundle = "ui", Phase = 0, Labels = new[] { "ui" } },
            };
            var bundles = new List<CatalogBuilder.BuiltBundle>
            {
                new CatalogBuilder.BuiltBundle { Name = "shared", Hash = "h_shared", DirectDependencies = new string[0] },
                new CatalogBuilder.BuiltBundle { Name = "ui", Hash = "h_ui", DirectDependencies = new[] { "shared" } },
            };
            var packs = new List<CatalogBuilder.PackRecord>
            {
                new CatalogBuilder.PackRecord { Name = "core", Bundles = new[] { "ui" } },
            };

            var catalog = CatalogJson.Parse(CatalogBuilder.ToJson(addresses, bundles, packs));

            Assert.IsTrue(catalog.TryResolveAsset("ui/menu", out var asset));
            Assert.AreEqual("ui", asset.Bundle);
            Assert.AreEqual("ui/menu", asset.AssetName);
            CollectionAssert.AreEqual(new[] { "ui" }, asset.Labels, "labels survive the build → JSON → parse round-trip");
            Assert.IsTrue(catalog.TryGetBundle("ui", out var ui));
            Assert.AreEqual("h_ui", ui.Hash);

            var closure = catalog.GetBundleClosure("ui");
            Assert.AreEqual("shared", closure[0].Name);
            Assert.AreEqual("ui", closure[1].Name);

            // The pack survives the build → JSON → parse round-trip, and its closure pulls in the member's deps.
            Assert.IsTrue(catalog.TryGetPack("core", out var core));
            CollectionAssert.AreEqual(new[] { "ui" }, core.Bundles, "pack members survive");
            var packClosure = new List<string>();
            foreach (var b in catalog.GetPackClosure("core")) packClosure.Add(b.Name);
            CollectionAssert.AreEqual(new[] { "shared", "ui" }, packClosure, "pack closure is deps-first");
        }
    }
}
