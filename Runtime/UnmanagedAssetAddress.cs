using System;
using Unity.Collections;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Burst-friendly value form of <see cref="AssetAddress"/>: the address is held in a
    /// <see cref="FixedString128Bytes"/> so it can be stored in components and passed through jobs
    /// without managed allocation. Convert to a managed <see cref="AssetAddress"/> at the call into
    /// <see cref="AssetManager"/> (which works in strings). Addresses longer than 128 UTF-8 bytes
    /// are rejected at construction.
    /// </summary>
    public readonly struct UnmanagedAssetAddress : IEquatable<UnmanagedAssetAddress>
    {
        public readonly FixedString128Bytes Value;

        public UnmanagedAssetAddress(FixedString128Bytes value) { Value = value; }

        public UnmanagedAssetAddress(string value)
        {
            var fs = new FixedString128Bytes();
            // CopyError.Truncation when the source exceeds the fixed capacity — refuse rather than
            // silently address the wrong asset.
            if (value != null && fs.CopyFrom(value) == CopyError.Truncation)
                throw new ArgumentException("Address exceeds 128 UTF-8 bytes: " + value, nameof(value));
            Value = fs;
        }

        public bool IsValid => !Value.IsEmpty;

        public AssetAddress ToManaged() => new AssetAddress(Value.ToString());

        // Explicit (not implicit) from string: AssetAddress already converts implicitly from string, so an
        // implicit one here would make LoadAssetAsync("literal") ambiguous between the managed/unmanaged overloads.
        // The Burst path constructs UnmanagedAssetAddress deliberately, so it never needs the implicit form.
        public static explicit operator UnmanagedAssetAddress(string value) => new UnmanagedAssetAddress(value);
        public static explicit operator AssetAddress(UnmanagedAssetAddress address) => address.ToManaged();

        public bool Equals(UnmanagedAssetAddress other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is UnmanagedAssetAddress other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }
}
