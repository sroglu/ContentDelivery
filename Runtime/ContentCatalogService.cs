using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>The wiring an app supplies once so the service can resolve, download and load content.</summary>
    public sealed class ContentServiceOptions
    {
        public IDownloadTransport Transport;   // null → the ContentDelivery default transport
        public IContentHasher Hasher;          // MUST match the hasher the catalog was built with
        public string PlatformFolder;          // null → ContentPlatform.ActivePlatformFolder()
        public string CacheDirectory;          // null → persistentDataPath/AssetBundles/<platform> (downloaded root)
    }

    /// <summary>Outcome of an app-driven catalog acquisition + load.</summary>
    public readonly struct CatalogLoadResult
    {
        public readonly bool Success;
        public readonly CatalogSource Source;
        public readonly string Error;   // null on success
        public CatalogLoadResult(bool success, CatalogSource source, string error)
        {
            Success = success; Source = source; Error = error;
        }
    }

    /// <summary>
    /// The app-owned catalog: a global singleton the app initializes with an already-deserialized
    /// <see cref="Catalog"/>, then loads assets from synchronously. It owns the resolution index
    /// (<see cref="ContentCatalogIndex"/>), the blocking bundle registry (<see cref="SyncBundleRegistry"/>) and the
    /// remote acquisition/download flow. A new catalog replaces the prior one entirely (bundles unloaded, index
    /// rebuilt — no merge). Pure C# service object (not a MonoBehaviour); main-thread only.
    /// </summary>
    public sealed class ContentCatalogService
    {
        /// <summary>The active instance; set by <see cref="Initialize"/>. Loading before init throws (fail-fast).</summary>
        public static ContentCatalogService Current { get; private set; }

        private readonly ContentCatalogIndex _index;
        private readonly SyncBundleRegistry _registry;
        private readonly ContentServiceOptions _options;
        private readonly string _catalogFileNameWithoutExtension;

        // Per-address load state: the closure it acquired + how many references it holds (for symmetric Unload).
        private readonly Dictionary<string, HeldLoad> _held = new Dictionary<string, HeldLoad>(StringComparer.Ordinal);
        // Instance id → the address whose bundle refcount it holds (released on Destroy).
        private readonly Dictionary<int, string> _instances = new Dictionary<int, string>();

        private struct HeldLoad { public IReadOnlyList<CatalogBundle> Closure; public int Count; }

        private ContentCatalogService(string catalogFileNameWithoutExtension, Catalog catalog, ContentServiceOptions options)
        {
            _catalogFileNameWithoutExtension = catalogFileNameWithoutExtension;
            _options = options;
            _index = new ContentCatalogIndex(catalog);
            _registry = new SyncBundleRegistry(ResolveLoadablePath);
        }

        /// <summary>The catalog file name (without extension) this instance was initialized with.</summary>
        public string CatalogFileName => _catalogFileNameWithoutExtension;

        /// <summary>The active catalog (bundle graph, packs, version).</summary>
        public Catalog Catalog => _index.Catalog;

        // ---- capability 3: app-owns-catalog init (NO file IO; validates + indexes + configures paths) ----

        /// <summary>
        /// Installs <paramref name="catalog"/> (already resolved/deserialized by the app) as the active content
        /// catalog: builds the resolution index and configures bundle path resolution. Does no file IO of its own.
        /// Any previously-initialized catalog is fully replaced — its loaded bundles are unloaded first.
        /// </summary>
        public static ContentCatalogService Initialize(
            string catalogFileNameWithoutExtension, Catalog catalog, ContentServiceOptions options)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (options == null) throw new ArgumentNullException(nameof(options));

            Current?.Teardown();
            var next = new ContentCatalogService(catalogFileNameWithoutExtension, catalog, Normalize(options));
            Current = next;
            return next;
        }

        private static ContentServiceOptions Normalize(ContentServiceOptions o)
        {
            string platform = o.PlatformFolder ?? ContentPlatform.ActivePlatformFolder();
            return new ContentServiceOptions
            {
                Transport = o.Transport ?? ContentDeliveryBootstrap.DefaultTransport,
                Hasher = o.Hasher ?? new XxHash3ContentHasher(),
                PlatformFolder = platform,
                CacheDirectory = o.CacheDirectory ?? ContentPlatform.GetRemoteAssetBundlePath(platform),
            };
        }

        private void Teardown()
        {
            _registry.UnloadAll();
            _held.Clear();
            _instances.Clear();
        }

        // ---- capability 2: synchronous load / instantiate ----

        /// <summary>
        /// Loads <paramref name="address"/> and its dependencies synchronously, taking <paramref name="count"/>
        /// references. Resolves an alias/sub-asset (sprite-in-atlas) to its main asset + cached sub-name. Returns
        /// null on a clean miss (unknown address, or the asset is absent from its bundle). Blocks the main thread.
        /// </summary>
        public T LoadAsset<T>(AssetAddress address, int count = 1) where T : UnityEngine.Object
        {
            if (!_index.TryResolve(address.Value, out var resolved)) return null;

            var closure = _index.Catalog.GetBundleClosure(resolved.Bundle);
            AssetBundle primary = _registry.AcquireClosure(closure, count);

            T asset = resolved.HasSubAsset
                ? ExtractSubAsset<T>(primary, resolved.MainAssetName, resolved.SubAssetName)
                : primary.LoadAsset<T>(resolved.MainAssetName);

            if (asset == null)
            {
                _registry.ReleaseClosure(closure, count); // don't pin a closure for a load that yielded nothing
                return null;
            }

            RecordHeld(address.Value, closure, count);
            return asset;
        }

        // Sprite-atlas / composite: load the main object's sub-assets and pick the one whose name was cached.
        private static T ExtractSubAsset<T>(AssetBundle bundle, string mainName, string subName) where T : UnityEngine.Object
        {
            foreach (var obj in bundle.LoadAssetWithSubAssets<T>(mainName))
                if (obj != null && obj.name == subName) return obj;
            return null;
        }

        /// <summary>Loads a GameObject address and instantiates it (the instance holds a reference until destroyed).</summary>
        public GameObject Instantiate(AssetAddress address) => InstantiateInternal(address, go => UnityEngine.Object.Instantiate(go));

        public GameObject Instantiate(AssetAddress address, Transform parent) =>
            InstantiateInternal(address, go => UnityEngine.Object.Instantiate(go, parent));

        public GameObject Instantiate(AssetAddress address, Vector3 position, Quaternion rotation, Transform parent) =>
            InstantiateInternal(address, go => UnityEngine.Object.Instantiate(go, position, rotation, parent));

        /// <summary>Loads + instantiates an address and returns the component <typeparamref name="T"/> on the instance.</summary>
        public T Instantiate<T>(AssetAddress address) where T : Component
        {
            GameObject instance = Instantiate(address);
            return instance != null ? instance.GetComponent<T>() : null;
        }

        private GameObject InstantiateInternal(AssetAddress address, Func<GameObject, GameObject> instantiate)
        {
            GameObject prefab = LoadAsset<GameObject>(address);
            if (prefab == null) return null;
            GameObject instance = instantiate(prefab);
            _instances[instance.GetInstanceID()] = address.Value;
            return instance;
        }

        /// <summary>Destroys an instance from <see cref="Instantiate(AssetAddress)"/>, releasing its bundle reference.</summary>
        public void Destroy(UnityEngine.Object instance)
        {
            if (instance == null) return; // Unity object may already be destroyed — boundary check
            int id = instance.GetInstanceID();
            if (_instances.TryGetValue(id, out string address))
            {
                _instances.Remove(id);
                Unload(address);
            }
            UnityEngine.Object.Destroy(instance);
        }

        /// <summary>Releases one reference taken by a prior <see cref="LoadAsset{T}"/>; unloads the bundle at zero.</summary>
        public void Unload(AssetAddress address)
        {
            if (!_held.TryGetValue(address.Value, out var held)) return;
            _registry.ReleaseClosure(held.Closure, 1);
            if (held.Count <= 1) _held.Remove(address.Value);
            else _held[address.Value] = new HeldLoad { Closure = held.Closure, Count = held.Count - 1 };
        }

        private void RecordHeld(string address, IReadOnlyList<CatalogBundle> closure, int count)
        {
            if (_held.TryGetValue(address, out var existing))
                _held[address] = new HeldLoad { Closure = existing.Closure, Count = existing.Count + count };
            else
                _held[address] = new HeldLoad { Closure = closure, Count = count };
        }

        // A bundle's loadable file: embedded (StreamingAssets) for local content, the downloaded cache for remote.
        private string ResolveLoadablePath(CatalogBundle bundle) =>
            bundle.Local
                ? Path.Combine(ContentPlatform.GetEmbeddedAssetBundlePath(_options.PlatformFolder), bundle.Hash)
                : Path.Combine(_options.CacheDirectory, string.IsNullOrEmpty(bundle.UncompressedHash) ? bundle.Hash : bundle.UncompressedHash);

        // ---- capability 7: bulk/conditional download orchestration ----

        /// <summary>
        /// Acquires the catalog named by <paramref name="remoteConfig"/> the cheapest way — the embedded copy if it
        /// already matches, else a cached copy, else a download — decodes it, and initializes the service with it.
        /// Fail-soft: returns a typed <see cref="CatalogLoadResult"/> instead of throwing on network/IO failure.
        /// </summary>
        public static async UniTask<CatalogLoadResult> AcquireCatalogAsync(
            RemoteContentConfig remoteConfig, ContentServiceOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var opts = Normalize(options);
            string required = remoteConfig.CatalogFileName;
            string nameNoExt = Path.GetFileNameWithoutExtension(required);

            var embedded = await EmbeddedCatalogReader.TryReadEmbeddedCatalogAsync(opts.PlatformFolder);
            string cachedPath = Path.Combine(opts.CacheDirectory, required);
            bool cached = File.Exists(cachedPath);

            var plan = CatalogAcquisitionPlan.Decide(required, embedded.Found ? embedded.FileName : null, cached);

            try
            {
                Catalog catalog;
                switch (plan)
                {
                    case CatalogSource.Embedded:
                        catalog = embedded.Catalog;
                        break;
                    case CatalogSource.Cached:
                        catalog = DecodeCatalogBytes(File.ReadAllBytes(cachedPath));
                        break;
                    default:
                        byte[] bytes = await opts.Transport.DownloadBytesAsync(remoteConfig.GetCatalogUrl(), cancellationToken);
                        Directory.CreateDirectory(opts.CacheDirectory);
                        File.WriteAllBytes(cachedPath, bytes);
                        catalog = DecodeCatalogBytes(bytes);
                        break;
                }

                Initialize(nameNoExt, catalog, opts);
                return new CatalogLoadResult(true, plan, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return new CatalogLoadResult(false, plan, e.Message);
            }
        }

        /// <summary>
        /// Downloads every remote bundle the active catalog needs that is not already resident (pack-grouped, then
        /// standalone), concurrently with retry + decompression + per-bundle hash validation. Progress is pollable
        /// via <paramref name="progress"/>. Requires a valid remote config; no-op result when nothing is missing.
        /// </summary>
        public async UniTask<CatalogContentResult> DownloadCatalogContentAsync(
            RemoteContentConfig remoteConfig,
            IProgress<SchedulerProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var downloader = new CatalogContentDownloader(
                _options.Transport, _options.CacheDirectory, remoteConfig, _options.Hasher);
            return await downloader.DownloadCatalogContentAsync(_index.Catalog, progress, cancellationToken).AsUniTask();
        }

        private static Catalog DecodeCatalogBytes(byte[] bytes) =>
            CatalogCodec.Decode(bytes, b => CatalogJson.Parse(Encoding.UTF8.GetString(b)));
    }
}
