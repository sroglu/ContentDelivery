using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>A trivial loadable asset for ref-count tests (instantiable, distinct instance ids).</summary>
    internal sealed class TestAsset : ScriptableObject { }

    /// <summary>
    /// In-memory <see cref="IAssetSource"/> double: resolves only addresses it was given, records load
    /// calls and releases, so AssetManager/AssetLoader ref-count behavior is observable without bundles.
    /// </summary>
    internal sealed class FakeAssetSource : IAssetSource
    {
        public readonly Dictionary<string, Object> Map = new Dictionary<string, Object>();
        public readonly List<string> Released = new List<string>();
        public int LoadCalls;

        public UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object
        {
            LoadCalls++;
            Map.TryGetValue(address.Value, out var asset);
            return UniTask.FromResult(asset as T);
        }

        public void Release(AssetAddress address) => Released.Add(address.Value);

        public TestAsset Add(string address)
        {
            var asset = ScriptableObject.CreateInstance<TestAsset>();
            asset.name = address;
            Map[address] = asset;
            return asset;
        }
    }

    /// <summary>An <see cref="IAssetSource"/> whose load always throws — to exercise the Failed+Error path.</summary>
    internal sealed class ThrowingAssetSource : IAssetSource
    {
        public UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object =>
            throw new System.InvalidOperationException("source failure for " + address.Value);

        public void Release(AssetAddress address) { }
    }
}
