using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Resolves an <see cref="AssetAddress"/> to an asset. Sources are tried in priority order;
    /// returning null lets the next source try. Remote-bundle / CDN sources plug in here later.
    /// </summary>
    public interface IAssetSource
    {
        UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object;

        /// <summary>
        /// Releases any source-side resources (e.g. ref-counted bundles) backing an address once its
        /// last reference is gone. Called by <see cref="AssetManager"/> when the address ref-count hits
        /// zero. Sources without backing resources (Resources) implement this as a no-op.
        /// </summary>
        void Release(AssetAddress address);
    }

    /// <summary>
    /// Loads assets from a <c>Resources</c> folder, treating the address as a Resources path. Demoted to
    /// a local-dev / StreamingAssets fallback behind <see cref="RemoteBundleAssetSource"/>: it has no
    /// per-address resources to free, so <see cref="Release"/> is a no-op (Resources are released in bulk
    /// via <c>Resources.UnloadUnusedAssets</c>, not per address).
    /// </summary>
    public sealed class ResourcesAssetSource : IAssetSource
    {
        public async UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object
        {
            if (!address.IsValid) return null;
            var request = Resources.LoadAsync<T>(address.Value);
            await request.ToUniTask();
            return request.asset as T;
        }

        public void Release(AssetAddress address) { }
    }
}
