using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Owner-scoped handle onto <see cref="AssetManager"/>: loads and instantiates by address, ref-counted,
    /// and releases everything it still holds — loaded addresses and live instances — on <see cref="Dispose"/>.
    /// The <see cref="FixedString64Bytes"/> id labels the owner for diagnostics. Main-thread only.
    /// </summary>
    public sealed class AssetLoader : IDisposable
    {
        private readonly FixedString64Bytes _id;
        private readonly AssetLoaderId _loaderId;
        private readonly HashSet<AssetAddress> _owned = new HashSet<AssetAddress>();
        private readonly HashSet<UnityEngine.Object> _instances = new HashSet<UnityEngine.Object>();

        public AssetLoader(FixedString64Bytes id) { _id = id; _loaderId = new AssetLoaderId(id); }

        public AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress address) where T : UnityEngine.Object
        {
            _owned.Add(address);
            return AssetManager.LoadAssetAsync<T>(address, _loaderId);
        }

        /// <summary>Burst-friendly overload: resolves the <see cref="UnmanagedAssetAddress"/> to a managed address.</summary>
        public AsyncAssetLoadHandle<T> LoadAssetAsync<T>(UnmanagedAssetAddress address) where T : UnityEngine.Object =>
            LoadAssetAsync<T>(address.ToManaged());

        /// <summary>Loads and instantiates a copy; the instance is owned by this loader until destroyed.</summary>
        public async UniTask<T> InstantiateAsync<T>(AssetAddress address) where T : UnityEngine.Object
        {
            var instance = await AssetManager.InstantiateAsync<T>(address, _loaderId);
            if (instance != null) _instances.Add(instance);
            return instance;
        }

        /// <summary>Releases this loader's reference to <paramref name="address"/>.</summary>
        public void UnloadAsset(AssetAddress address)
        {
            if (_owned.Remove(address)) AssetManager.UnloadAsset(address, _loaderId);
        }

        /// <summary>Destroys an instance from <see cref="InstantiateAsync{T}"/>, releasing its asset reference.</summary>
        public void Destroy(UnityEngine.Object instance)
        {
            if (instance == null) return;
            _instances.Remove(instance);
            AssetManager.Destroy(instance);
        }

        public void Dispose()
        {
            // Destroy instances first so their per-instance address references are released before we
            // unload the loader's own held addresses.
            foreach (var instance in _instances)
                if (instance != null) AssetManager.Destroy(instance);
            _instances.Clear();

            foreach (var address in _owned) AssetManager.UnloadAsset(address, _loaderId);
            _owned.Clear();
        }
    }
}
