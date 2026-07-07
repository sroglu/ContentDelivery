using System;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// A serializable, strongly-typed handle to a content address: a designer drops one in the inspector, the
    /// game loads through it. The only serialized state is <see cref="Address"/> — the same string a loader
    /// resolves — so an <see cref="AssetReference{T}"/> IS its address (implicitly convertible to
    /// <see cref="AssetAddress"/> and <see cref="string"/>). <typeparamref name="T"/> is the expected asset type,
    /// which the editor drawer uses to offer only valid addresses and callers use to load the right type.
    /// The runtime field is deliberately minimal; the inspector dropdown is an editor-only convenience layered on
    /// top (see <c>AssetReferenceDrawer</c>).
    /// </summary>
    [Serializable]
    public struct AssetReference<T> where T : UnityEngine.Object
    {
        [SerializeField] private string _address;

        public AssetReference(string address) { _address = address; }

        /// <summary>The content address this reference points at (the load key).</summary>
        public string Address => _address;

        /// <summary>Whether an address has been assigned (a boundary check on authored data, not lifecycle state).</summary>
        public bool HasAddress => !string.IsNullOrEmpty(_address);

        /// <summary>The address as an <see cref="AssetAddress"/>, ready to pass to a loader.</summary>
        public AssetAddress AsAddress => new AssetAddress(_address);

        public static implicit operator AssetAddress(AssetReference<T> reference) => reference.AsAddress;
        public static implicit operator string(AssetReference<T> reference) => reference._address;

        public override string ToString() => _address ?? "<unassigned>";
    }
}
