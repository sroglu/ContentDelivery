using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// An <see cref="IAssetSource"/> that resolves an address straight to the live project asset via
    /// <see cref="AssetDatabase"/> — no bundle build, no download, no cache. Registered ahead of
    /// <see cref="RemoteBundleAssetSource"/> in the Editor (see <see cref="EditorFastPathMode"/>) so day-to-day
    /// iteration always loads project truth, never a stale built bundle (the play-mode stale-bundle trap). An
    /// address the map doesn't know returns null so the chain falls through to the real bundle path. Editor-only.
    /// </summary>
    public sealed class EditorAssetSource : IAssetSource
    {
        // address → the project asset path that backs it (built from the authoring AssetGroups).
        private readonly IReadOnlyDictionary<string, string> _addressToPath;

        public EditorAssetSource(IReadOnlyDictionary<string, string> addressToPath)
        {
            _addressToPath = addressToPath;
        }

        public UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object
        {
            // Unknown here = not authored in any group → let a lower-priority source try (clean fall-through).
            if (!address.IsValid || !_addressToPath.TryGetValue(address.Value, out var path))
                return UniTask.FromResult<T>(null);

            // Sub-asset addresses ("main[sub]") pick the representation named `sub` from the asset file,
            // mirroring how the bundle path resolves them (same TrySplitSubAsset selector).
            if (RemoteBundleAssetSource.TrySplitSubAsset(address.Value, out _, out var sub))
            {
                foreach (var representation in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                    if (representation is T typed && representation.name == sub)
                        return UniTask.FromResult(typed);
                return UniTask.FromResult<T>(null);
            }

            return UniTask.FromResult(AssetDatabase.LoadAssetAtPath<T>(path));
        }

        // Nothing to free: the editor owns the project assets, this source only points at them.
        public void Release(AssetAddress address) { }
    }
}
