using System;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Addresses an asset by a stable string id (resolved by an <see cref="IAssetSource"/> —
    /// a Resources path today, a remote bundle entry later). Implicitly convertible from string.
    /// </summary>
    public readonly struct AssetAddress : IEquatable<AssetAddress>
    {
        public readonly string Value;
        public AssetAddress(string value) { Value = value; }

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public static implicit operator AssetAddress(string value) => new AssetAddress(value);

        public bool Equals(AssetAddress other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is AssetAddress other && Equals(other);
        public override int GetHashCode() => Value != null ? Value.GetHashCode() : 0;
        public override string ToString() => Value ?? "<invalid>";
    }
}
