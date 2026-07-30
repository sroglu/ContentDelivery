using System;
using Unity.Collections;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Identifies the owner of an asset reference so <see cref="AssetManager"/> can ref-count per loader, not just
    /// per address: two loaders holding the same address each carry their own count, and one disposing releases
    /// exactly its own references. Bare <c>AssetManager</c> calls that don't name an owner use <see cref="Global"/>.
    /// </summary>
    public readonly struct AssetLoaderId : IEquatable<AssetLoaderId>
    {
        public readonly FixedString64Bytes Value;

        public AssetLoaderId(FixedString64Bytes value) { Value = value; }

        public AssetLoaderId(string value)
        {
            var fs = new FixedString64Bytes();
            if (!string.IsNullOrEmpty(value)) fs.CopyFrom(value);
            Value = fs;
        }

        /// <summary>The shared owner for callers that go through <see cref="AssetManager"/> without a loader.</summary>
        public static readonly AssetLoaderId Global = new AssetLoaderId("<global>");

        public bool Equals(AssetLoaderId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is AssetLoaderId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }
}
