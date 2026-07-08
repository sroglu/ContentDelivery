using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Primary <see cref="IAssetSource"/>: resolves an address through the remote <see cref="Catalog"/>,
    /// provisions the owning bundle and its dependency closure (download → hash-verify → disk-cache via
    /// <see cref="BundleProvisioner"/>), loads the bundles ref-counted, and pulls the asset out by its
    /// in-bundle name. An address unknown to the catalog returns null so a lower-priority source (e.g.
    /// <see cref="ResourcesAssetSource"/>) can still serve it. A bundle present but missing the asset is
    /// treated as a miss — the closure is released and not pinned. Main-thread only.
    /// </summary>
    public sealed class RemoteBundleAssetSource : IAssetSource, IBundleMemorySource
    {
        // Spans this source's resolve→closure→bundle-asset-load; the registry's provision/load markers nest beneath.
        private static readonly ProfilerMarker s_loadMarker = new(ProfilerCategory.Loading, "ContentDelivery.LoadAsset");

        private readonly Catalog _catalog;
        private readonly LoadedBundleRegistry _registry;
        private readonly BundleProvisioner _provisioner;
        // Per-address closure held open so Release frees exactly what this load acquired.
        private readonly Dictionary<string, IReadOnlyList<CatalogBundle>> _held =
            new Dictionary<string, IReadOnlyList<CatalogBundle>>();
        // Scene-bundle closures held resident by ticket id (a scene keeps its bundle loaded while the scene lives).
        private readonly Dictionary<int, IReadOnlyList<CatalogBundle>> _heldScenes =
            new Dictionary<int, IReadOnlyList<CatalogBundle>>();
        private int _nextSceneTicket;

        /// <param name="baseUrl">CDN origin for Remote (post-launch-updatable) bundles.</param>
        /// <param name="localBaseUrl">
        /// Origin for Local (build-shipped) bundles; null defaults to the StreamingAssets content URL, where the
        /// build pipeline writes Local bundles. Remote-only catalogs can ignore it.
        /// </param>
        /// <param name="hasher">Content hasher; null = xxHash3. MUST match the hasher the catalog was built with.</param>
        public RemoteBundleAssetSource(
            Catalog catalog, IDownloadTransport transport, string cacheDirectory, string baseUrl,
            string localBaseUrl = null, IContentHasher hasher = null)
        {
            _catalog = catalog;
            _provisioner = new BundleProvisioner(
                transport, cacheDirectory, baseUrl, localBaseUrl ?? ContentDeliveryPaths.StreamingAssetsContentUrl,
                hasher: hasher ?? new XxHash3ContentHasher());
            _registry = new LoadedBundleRegistry(_provisioner);
        }

        public async UniTask<T> LoadAsync<T>(AssetAddress address) where T : Object
        {
            if (!address.IsValid) return null;
            if (!_catalog.TryResolveAsset(address.Value, out var entry)) return null;

            using var _ = s_loadMarker.Auto();

            var closure = _catalog.GetBundleClosure(entry.Bundle);
            var bundle = await _registry.AcquireClosureAsync(closure);

            var asset = await LoadFromBundle<T>(bundle, entry.AssetName);

            if (asset == null)
            {
                // Missing inside the bundle: don't keep the closure resident for a load that yielded
                // nothing — release and report a miss so the resolve stays retryable.
                _registry.ReleaseClosure(closure);
                return null;
            }

            _held[address.Value] = closure;
            return asset;
        }

        /// <summary>Reports the bundles this source currently holds resident (for the memory report).</summary>
        public IReadOnlyList<BundleMemoryRow> SnapshotBundleRows() => _registry.SnapshotMemoryRows();

        /// <summary>
        /// Loads the named asset from a bundle, resolving a sub-asset selector. An in-bundle name of the form
        /// <c>main[sub]</c> pulls <c>sub</c> out of the composite asset <c>main</c> (sprite-in-atlas, mesh-in-fbx)
        /// via <see cref="AssetBundle.LoadAssetWithSubAssetsAsync"/>; a plain name is a direct load.
        /// </summary>
        private static async UniTask<T> LoadFromBundle<T>(AssetBundle bundle, string assetName) where T : Object
        {
            if (TrySplitSubAsset(assetName, out string mainName, out string subName))
            {
                var subRequest = bundle.LoadAssetWithSubAssetsAsync<T>(mainName);
                await subRequest.ToUniTask();
                foreach (var obj in subRequest.allAssets)
                    if (obj is T typed && obj.name == subName) return typed;
                return null;
            }

            var request = bundle.LoadAssetAsync<T>(assetName);
            await request.ToUniTask();
            return request.asset as T;
        }

        // "main[sub]" → ("main","sub"); anything else → false. A trailing ']' with a non-empty selector is required.
        internal static bool TrySplitSubAsset(string assetName, out string main, out string sub)
        {
            main = assetName; sub = null;
            if (string.IsNullOrEmpty(assetName) || assetName[assetName.Length - 1] != ']') return false;
            int open = assetName.IndexOf('[');
            if (open <= 0) return false;
            main = assetName.Substring(0, open);
            sub = assetName.Substring(open + 1, assetName.Length - open - 2);
            return sub.Length > 0;
        }

        /// <summary>
        /// Provisions (download → verify → disk-cache, without loading into memory) every bundle backing an asset
        /// whose phase is at or below <paramref name="maxPhaseInclusive"/>, dependencies included. This is the
        /// phased-delivery primitive: <c>PreloadAsync((int)AssetPhase.Essential)</c> brings boot content onto disk
        /// before the rest, so a later <see cref="LoadAsync{T}"/> is a cache hit with no network wait.
        /// </summary>
        public async UniTask PreloadAsync(int maxPhaseInclusive)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var asset in _catalog.AssetsUpToPhase(maxPhaseInclusive))
            {
                var closure = _catalog.GetBundleClosure(asset.Bundle);
                for (int i = 0; i < closure.Count; i++)
                    if (seen.Add(closure[i].Hash))
                        await _provisioner.EnsureBundleAsync(closure[i]).AsUniTask();
            }
        }

        /// <summary>Convenience overload taking the <see cref="AssetPhase"/> band directly.</summary>
        public UniTask PreloadAsync(AssetPhase maxPhaseInclusive) => PreloadAsync((int)maxPhaseInclusive);

        /// <summary>
        /// Like <see cref="PreloadAsync(int)"/>, but provisions one phase at a time IN ASCENDING ORDER, gating each
        /// phase on the previous completing: every Essential bundle is on disk before any Early bundle starts, Early
        /// before Standard, and so on up to <paramref name="maxPhaseInclusive"/>. A bundle shared across phases is
        /// provisioned once, in the earliest phase that needs it. Use when later, less-critical content must not
        /// compete for bandwidth/IO until the earlier content has landed. (A game layers its own content-specific
        /// prerequisites on top of this via labels + <see cref="PreloadAsync(int)"/>; this primitive is ordering only.)
        /// </summary>
        public async UniTask PreloadPhasesSequentialAsync(int maxPhaseInclusive)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var phase in _catalog.PhasesUpTo(maxPhaseInclusive))
            {
                // Awaited inside this loop, so the whole phase lands before the next phase iteration begins.
                foreach (var asset in _catalog.AssetsInPhase(phase))
                {
                    var closure = _catalog.GetBundleClosure(asset.Bundle);
                    for (int i = 0; i < closure.Count; i++)
                        if (seen.Add(closure[i].Hash))
                            await _provisioner.EnsureBundleAsync(closure[i]).AsUniTask();
                }
            }
        }

        /// <summary>Convenience overload taking the <see cref="AssetPhase"/> band directly.</summary>
        public UniTask PreloadPhasesSequentialAsync(AssetPhase maxPhaseInclusive) =>
            PreloadPhasesSequentialAsync((int)maxPhaseInclusive);

        public void Release(AssetAddress address)
        {
            if (!address.IsValid || !_held.TryGetValue(address.Value, out var closure)) return;
            _held.Remove(address.Value);
            _registry.ReleaseClosure(closure);
        }

        /// <summary>
        /// Provisions + loads the bundle (and its dependency closure) that packs the scene at
        /// <paramref name="sceneAddress"/> and returns a ticket carrying the in-bundle <c>scenePath</c> the engine
        /// scene loader needs, plus a token to release the bundle with. A scene is NOT a loadable
        /// <see cref="Object"/> — its bundle must be resident for the engine's own scene load to find it — so this
        /// keeps the closure resident until <see cref="ReleaseSceneBundle"/>. Use <see cref="ContentSceneLoader"/>
        /// to load + unload the scene together with its bundle. Returns an invalid ticket if the address is unknown.
        /// </summary>
        public async UniTask<SceneBundleTicket> AcquireSceneBundleAsync(AssetAddress sceneAddress)
        {
            if (!sceneAddress.IsValid || !_catalog.TryResolveAsset(sceneAddress.Value, out var entry))
                return SceneBundleTicket.Invalid;

            var closure = _catalog.GetBundleClosure(entry.Bundle);
            var bundle = await _registry.AcquireClosureAsync(closure);

            string scenePath = ResolveScenePath(bundle, entry.AssetName, sceneAddress);
            if (string.IsNullOrEmpty(scenePath))
            {
                _registry.ReleaseClosure(closure); // no scene in the bundle — don't pin it
                return SceneBundleTicket.Invalid;
            }

            int token = ++_nextSceneTicket;
            _heldScenes[token] = closure;
            return new SceneBundleTicket(token, scenePath);
        }

        /// <summary>Releases the bundle a <see cref="AcquireSceneBundleAsync"/> ticket holds resident. Idempotent.</summary>
        public void ReleaseSceneBundle(SceneBundleTicket ticket)
        {
            if (!ticket.IsValid || !_heldScenes.TryGetValue(ticket.Token, out var closure)) return;
            _heldScenes.Remove(ticket.Token);
            _registry.ReleaseClosure(closure);
        }

        // A scene bundle exposes its scenes as asset paths. Prefer the one whose scene name matches the catalog's
        // in-bundle asset name; otherwise take the only/first path (warning when the bundle packs several).
        private static string ResolveScenePath(AssetBundle bundle, string assetName, AssetAddress address)
        {
            string[] paths = bundle.GetAllScenePaths();
            if (paths == null || paths.Length == 0) return null;

            if (!string.IsNullOrEmpty(assetName))
                for (int i = 0; i < paths.Length; i++)
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(paths[i]), assetName, System.StringComparison.Ordinal))
                        return paths[i];

            if (paths.Length > 1)
                Debug.LogWarning($"[ContentDelivery] Scene bundle for '{address}' packs {paths.Length} scenes; using '{paths[0]}'.");
            return paths[0];
        }
    }
}
