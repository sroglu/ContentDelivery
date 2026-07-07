using System.Collections.Generic;
using NUnit.Framework;
using PFound.ContentDelivery.Editor;
using UnityEngine;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="BuildScopeFilter.Apply"/> — the pre-build scope selection over the gathered
    /// <see cref="AssetGroup"/>s. Uses pure in-memory ScriptableObjects (no project assets, no importer writes), so
    /// it sidesteps the [OneTimeSetUp] importer-persistence trap entirely. Membership is asserted as a set.
    /// </summary>
    public sealed class BuildScopeFilterTests
    {
        private AssetGroup _a, _b, _c;

        [SetUp]
        public void Create()
        {
            _a = MakeGroup("A", excludeInProduction: false);
            _b = MakeGroup("B", excludeInProduction: true);  // the production-excluded one
            _c = MakeGroup("C", excludeInProduction: false);
        }

        [TearDown]
        public void Destroy()
        {
            Object.DestroyImmediate(_a);
            Object.DestroyImmediate(_b);
            Object.DestroyImmediate(_c);
        }

        [Test]
        public void AllGroups_ReturnsEveryGroup()
        {
            var result = BuildScopeFilter.Apply(new[] { _a, _b, _c }, BuildScope.AllGroups);
            CollectionAssert.AreEquivalent(new[] { _a, _b, _c }, result);
        }

        [Test]
        public void AllGroups_IgnoresSelected()
        {
            var result = BuildScopeFilter.Apply(new[] { _a, _b, _c }, BuildScope.AllGroups,
                new HashSet<AssetGroup> { _a });
            CollectionAssert.AreEquivalent(new[] { _a, _b, _c }, result, "AllGroups ignores the selected set");
        }

        [Test]
        public void ExcludeInProd_DropsExcludedGroups()
        {
            var result = BuildScopeFilter.Apply(new[] { _a, _b, _c }, BuildScope.ExcludeInProd);
            CollectionAssert.AreEquivalent(new[] { _a, _c }, result, "B is dropped (ExcludeInProduction == true)");
        }

        [Test]
        public void ExcludeInProd_WithNoExcludedGroups_ReturnsAll()
        {
            var result = BuildScopeFilter.Apply(new[] { _a, _c }, BuildScope.ExcludeInProd);
            CollectionAssert.AreEquivalent(new[] { _a, _c }, result, "no group flagged → every group kept");
        }

        [Test]
        public void OnlySelected_ReturnsExactlyTheSelectedSet()
        {
            var result = BuildScopeFilter.Apply(new[] { _a, _b, _c }, BuildScope.OnlySelected,
                new HashSet<AssetGroup> { _a, _c });
            CollectionAssert.AreEquivalent(new[] { _a, _c }, result);
        }

        [Test]
        public void OnlySelected_WithNullSelection_ReturnsEmpty()
        {
            // Documented behavior in BuildScope.cs: the guard `selected != null && selected.Contains(...)` makes a
            // null selection match nothing — it returns an empty list, it does NOT throw.
            var result = BuildScopeFilter.Apply(new[] { _a, _b, _c }, BuildScope.OnlySelected, null);
            Assert.IsEmpty(result);
        }

        private static AssetGroup MakeGroup(string name, bool excludeInProduction)
        {
            var g = ScriptableObject.CreateInstance<AssetGroup>();
            g.name = name;
            g.BundleName = name;
            g.ExcludeInProduction = excludeInProduction;
            return g;
        }
    }
}
