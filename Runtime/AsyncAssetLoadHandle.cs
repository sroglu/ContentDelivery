using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Phase of an asset load. The wait/bridge keys off the <em>terminal</em> set (<see cref="Loaded"/> or
    /// <see cref="Failed"/>) only — never off <see cref="NotLoaded"/>, so a freshly-created handle that hasn't
    /// started can't be mistaken for "done".
    /// </summary>
    public enum AssetLoadingStatus
    {
        /// <summary>Not started yet — NOT terminal (closes the pre-start race).</summary>
        NotLoaded,
        /// <summary>Resolving + provisioning the bundle dependency closure.</summary>
        LoadingBundles,
        /// <summary>Bundle(s) ready; pulling the asset out.</summary>
        LoadingAsset,
        /// <summary>Success — the asset resolved (terminal).</summary>
        Loaded,
        /// <summary>Terminal non-success: a clean miss (Error == null, retryable) or a pipeline error (Error != null).</summary>
        Failed,
    }

    /// <summary>Mutable backing state for one load; the handle is a zero-alloc value wrapper over this.</summary>
    internal sealed class AssetLoadOperation<T> where T : UnityEngine.Object
    {
        public AssetLoadingStatus Status = AssetLoadingStatus.NotLoaded;
        public T Asset;
        public Exception Error;
        public AssetAddress Address;
        public AssetLoaderId Loader; // owner whose ref-count Release() drops
    }

    /// <summary>
    /// Allocation-free value handle onto an in-flight or completed asset load. Cheap to pass around and store
    /// (e.g. in components); poll <see cref="Status"/> / <see cref="IsDone"/> / <see cref="Result"/> with no async
    /// machinery, or use the optional UniTask bridge to await. The bridge frame-polls the load status — it does
    /// NOT wrap an AsyncOperation awaiter — and completes on the terminal set <see cref="AssetLoadingStatus.Loaded"/>
    /// or <see cref="AssetLoadingStatus.Failed"/>. The handle also carries ref-counted <see cref="Release"/>.
    /// </summary>
    public readonly struct AsyncAssetLoadHandle<T> where T : UnityEngine.Object
    {
        private readonly AssetLoadOperation<T> _op;

        internal AsyncAssetLoadHandle(AssetLoadOperation<T> op) { _op = op; }

        public AssetLoadingStatus Status => _op != null ? _op.Status : AssetLoadingStatus.NotLoaded;

        public bool IsTerminal
        {
            get { var s = Status; return s == AssetLoadingStatus.Loaded || s == AssetLoadingStatus.Failed; }
        }

        /// <summary>True once the load reached a terminal state (loaded or failed).</summary>
        public bool IsDone => IsTerminal;

        /// <summary>The resolved asset; null until <see cref="Loaded"/>, and null on a miss/failure.</summary>
        public T Result => _op != null ? _op.Asset : null;

        /// <summary>Optional UniTask bridge — completes when the load is terminal. Frame-polls status.</summary>
        public UniTask Task
        {
            get
            {
                var op = _op;
                if (op == null || op.Status == AssetLoadingStatus.Loaded || op.Status == AssetLoadingStatus.Failed)
                    return UniTask.CompletedTask;
                return UniTask.WaitUntil(() => op.Status == AssetLoadingStatus.Loaded || op.Status == AssetLoadingStatus.Failed);
            }
        }

        /// <summary>
        /// Await the handle for the asset. Returns the asset on success, null on a clean miss, and rethrows the
        /// captured exception when the load failed with an error.
        /// </summary>
        public UniTask<T>.Awaiter GetAwaiter() => AwaitValue().GetAwaiter();

        private async UniTask<T> AwaitValue()
        {
            var op = _op;
            if (op == null) return null;
            if (op.Status != AssetLoadingStatus.Loaded && op.Status != AssetLoadingStatus.Failed)
                await UniTask.WaitUntil(() => op.Status == AssetLoadingStatus.Loaded || op.Status == AssetLoadingStatus.Failed);
            if (op.Status == AssetLoadingStatus.Failed && op.Error != null) throw op.Error;
            return op.Asset;
        }

        /// <summary>Releases this handle's reference on the address (ref-counted unload). Safe once terminal.</summary>
        public void Release()
        {
            if (_op != null && _op.Address.IsValid) AssetManager.UnloadAsset(_op.Address, _op.Loader);
        }
    }
}
