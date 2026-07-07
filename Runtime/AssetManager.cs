using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// Process-wide asset resolution: an ordered source chain (the runtime catalog) plus ref-counted
    /// caching by address. The remote bundle source registers ahead of the <see cref="ResourcesAssetSource"/>
    /// fallback. Instantiated copies hold a reference on their source asset until destroyed, so the
    /// backing bundle stays resident exactly as long as something it produced is alive. Main-thread only.
    /// </summary>
    public static class AssetManager
    {
        private sealed class Entry
        {
            public Object Asset;
            public int RefCount; // total across all loaders; the entry is freed when this hits 0
            public IAssetSource Source;
            // Per-owner breakdown so each loader releases exactly what it took.
            public readonly Dictionary<AssetLoaderId, int> RefByLoader = new Dictionary<AssetLoaderId, int>();
        }

        // instanceID -> the (address, owner) whose ref-count this instance holds (released on Destroy).
        private readonly struct InstanceRef
        {
            public readonly AssetAddress Address;
            public readonly AssetLoaderId Loader;
            public InstanceRef(AssetAddress address, AssetLoaderId loader) { Address = address; Loader = loader; }
        }

        private static readonly List<IAssetSource> s_sources = new List<IAssetSource> { new ResourcesAssetSource() };
        private static readonly Dictionary<AssetAddress, Entry> s_entries = new Dictionary<AssetAddress, Entry>();
        private static readonly Dictionary<int, InstanceRef> s_instances = new Dictionary<int, InstanceRef>();

        // Wraps the whole resolve (cache check + source-chain walk + load) — the outermost ContentDelivery
        // load span; the remote source and bundle registry nest their own markers beneath it.
        private static readonly ProfilerMarker s_resolveMarker = new(ProfilerCategory.Loading, "ContentDelivery.Resolve");

        /// <summary>Registers a higher-priority source (tried before the defaults).</summary>
        public static void RegisterSource(IAssetSource source)
        {
            if (source != null) s_sources.Insert(0, source);
        }

        /// <summary>Removes a previously-registered source (e.g. the editor fast-path source when toggled off).
        /// Returns whether it was present.</summary>
        public static bool UnregisterSource(IAssetSource source) => source != null && s_sources.Remove(source);

        public static AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress address) where T : Object =>
            LoadAssetAsync<T>(address, AssetLoaderId.Global);

        /// <summary>Loads <paramref name="address"/> on behalf of <paramref name="loader"/> (per-owner ref-count).</summary>
        public static AsyncAssetLoadHandle<T> LoadAssetAsync<T>(AssetAddress address, AssetLoaderId loader) where T : Object
        {
            var op = new AssetLoadOperation<T> { Address = address, Loader = loader, Status = AssetLoadingStatus.LoadingAsset };
            DriveLoad(op).Forget();
            return new AsyncAssetLoadHandle<T>(op);
        }

        /// <summary>Burst-friendly overload: resolves the <see cref="UnmanagedAssetAddress"/> to a managed address.</summary>
        public static AsyncAssetLoadHandle<T> LoadAssetAsync<T>(UnmanagedAssetAddress address) where T : Object =>
            LoadAssetAsync<T>(address.ToManaged(), AssetLoaderId.Global);

        public static AsyncAssetLoadHandle<T> LoadAssetAsync<T>(UnmanagedAssetAddress address, AssetLoaderId loader) where T : Object =>
            LoadAssetAsync<T>(address.ToManaged(), loader);

        // Drives one load to a terminal status. A resolve to null is a clean miss (Failed, no Error); an exception
        // in the pipeline is a Failed with the captured Error so the awaiter can rethrow it.
        private static async UniTaskVoid DriveLoad<T>(AssetLoadOperation<T> op) where T : Object
        {
            try
            {
                var asset = await LoadInternal<T>(op.Address, op.Loader);
                op.Asset = asset;
                op.Status = asset != null ? AssetLoadingStatus.Loaded : AssetLoadingStatus.Failed;
            }
            catch (System.Exception e)
            {
                op.Error = e;
                op.Status = AssetLoadingStatus.Failed;
            }
        }

        public static void UnloadAsset(AssetAddress address) => UnloadAsset(address, AssetLoaderId.Global);

        /// <summary>Releases <paramref name="loader"/>'s reference on <paramref name="address"/>.</summary>
        public static void UnloadAsset(AssetAddress address, AssetLoaderId loader)
        {
            if (!s_entries.TryGetValue(address, out var entry)) return;

            // Drop this owner's share; ignore an over-release from a loader that holds nothing.
            if (entry.RefByLoader.TryGetValue(loader, out int owned))
            {
                if (owned <= 1) entry.RefByLoader.Remove(loader);
                else entry.RefByLoader[loader] = owned - 1;
            }

            if (--entry.RefCount > 0) return;
            s_entries.Remove(address);
            // Let the resolving source free its backing resources (ref-counted bundle closure, etc.).
            entry.Source?.Release(address);
        }

        public static bool IsAssetLoaded(AssetAddress address) => s_entries.ContainsKey(address);

        /// <summary>Clears all cached entries, instances and registered sources (default Resources re-added). Tests only.</summary>
        internal static void ResetForTests()
        {
            s_entries.Clear();
            s_instances.Clear();
            s_sources.Clear();
            s_sources.Add(new ResourcesAssetSource());
        }

        /// <summary>
        /// Loads <paramref name="address"/> (taking a reference) and instantiates a copy. The instance
        /// holds the reference until passed to <see cref="Destroy"/>; null if the address cannot resolve.
        /// </summary>
        public static UniTask<T> InstantiateAsync<T>(AssetAddress address) where T : Object =>
            InstantiateAsync<T>(address, AssetLoaderId.Global);

        public static async UniTask<T> InstantiateAsync<T>(AssetAddress address, AssetLoaderId loader) where T : Object
        {
            var asset = await LoadInternal<T>(address, loader);
            if (asset == null) return null;
            var instance = Object.Instantiate(asset);
            s_instances[instance.GetInstanceID()] = new InstanceRef(address, loader);
            return instance;
        }

        /// <summary>Destroys an instance from <see cref="InstantiateAsync{T}"/>, releasing its asset reference.</summary>
        public static void Destroy(Object instance)
        {
            if (instance == null) return;
            int id = instance.GetInstanceID();
            if (s_instances.TryGetValue(id, out var inst))
            {
                s_instances.Remove(id);
                UnloadAsset(inst.Address, inst.Loader);
            }
            // Object.Destroy is deferred and throws in edit mode; DestroyImmediate is the edit-mode/tooling path.
            if (Application.isPlaying) Object.Destroy(instance);
            else Object.DestroyImmediate(instance);
        }

        private static async UniTask<T> LoadInternal<T>(AssetAddress address, AssetLoaderId loader) where T : Object
        {
            // Marker spans the async resolve so the Profiler attributes the full time-to-resolve here.
            using var _ = s_resolveMarker.Auto();

            if (s_entries.TryGetValue(address, out var cached))
            {
                cached.RefCount++;
                AddLoaderRef(cached, loader);
                return cached.Asset as T;
            }

            T asset = null;
            IAssetSource resolvedBy = null;
            for (int i = 0; i < s_sources.Count; i++)
            {
                asset = await s_sources[i].LoadAsync<T>(address);
                if (asset != null) { resolvedBy = s_sources[i]; break; }
            }

            // Do NOT cache a failed/missing resolve: a miss must stay retryable and report
            // IsAssetLoaded == false (caching null would pin the address as "loaded" forever).
            if (asset != null)
            {
                var entry = new Entry { Asset = asset, RefCount = 1, Source = resolvedBy };
                AddLoaderRef(entry, loader);
                s_entries[address] = entry;
            }
            return asset;
        }

        private static void AddLoaderRef(Entry entry, AssetLoaderId loader)
        {
            entry.RefByLoader.TryGetValue(loader, out int n);
            entry.RefByLoader[loader] = n + 1;
        }

        /// <summary>How many references <paramref name="loader"/> currently holds on <paramref name="address"/>.</summary>
        public static int RefCountFor(AssetAddress address, AssetLoaderId loader) =>
            s_entries.TryGetValue(address, out var e) && e.RefByLoader.TryGetValue(loader, out int n) ? n : 0;

        /// <summary>The live source chain, for the memory reporter to collect bundle residency from.</summary>
        internal static IReadOnlyList<IAssetSource> Sources => s_sources;

        /// <summary>
        /// Snapshots every resident asset entry into detached <see cref="AssetMemoryRow"/>s (address, type, total
        /// and per-owner ref-counts, resolving source). When <paramref name="deep"/>, also measures each asset's
        /// runtime memory via the Profiler; otherwise the size is left unmeasured (-1).
        /// </summary>
        internal static IReadOnlyList<AssetMemoryRow> SnapshotAssetRows(bool deep)
        {
            var rows = new List<AssetMemoryRow>(s_entries.Count);
            foreach (var kv in s_entries)
            {
                var entry = kv.Value;
                var byLoader = new List<LoaderRef>(entry.RefByLoader.Count);
                foreach (var lr in entry.RefByLoader)
                    byLoader.Add(new LoaderRef(lr.Key.ToString(), lr.Value));

                string typeName = entry.Asset != null ? entry.Asset.GetType().Name : "null";
                string source = entry.Source != null ? entry.Source.GetType().Name : "none";
                rows.Add(new AssetMemoryRow(
                    kv.Key.Value, typeName, entry.RefCount, source, byLoader,
                    deep ? RuntimeMemorySizeOf(entry.Asset) : -1));
            }
            return rows;
        }

        // The deep-pass size is meaningful only where the Profiler tracks runtime sizes (editor / dev builds);
        // elsewhere it is reported as unmeasured rather than a misleading zero.
        private static long RuntimeMemorySizeOf(Object asset)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return asset != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(asset) : 0;
#else
            return -1;
#endif
        }
    }
}
