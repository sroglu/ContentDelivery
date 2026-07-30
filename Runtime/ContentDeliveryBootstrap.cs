using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery
{
    /// <summary>Outcome of an async, fail-soft ContentDelivery boot with catalog fallback.</summary>
    public readonly struct ContentDeliveryInitResult
    {
        public readonly bool Success;
        public readonly RemoteBundleAssetSource Source; // null on failure
        public readonly CatalogSource Origin;           // which branch the catalog came from: Embedded | Cached | Download
        public readonly string Error;                   // null on success

        public ContentDeliveryInitResult(bool success, RemoteBundleAssetSource source, CatalogSource origin, string error)
        {
            Success = success;
            Source = source;
            Origin = origin;
            Error = error;
        }
    }

    /// <summary>
    /// Entry point that wires the remote content system up: picks the default download transport and, given a
    /// catalog, registers a <see cref="RemoteBundleAssetSource"/> as the primary <see cref="IAssetSource"/>.
    /// <para>
    /// The default transport is <see cref="UnityWebRequestTransport"/> (dependency-free). When the BestHTTP
    /// adapter assembly is compiled in (the <c>PFOUND_BESTHTTP</c> define), it self-registers
    /// <c>BestHttpTransport</c> here at startup, making BestHTTP the default — without the foundation taking a
    /// hard reference to the optional library.
    /// </para>
    /// </summary>
    public static class ContentDeliveryBootstrap
    {
        private static IDownloadTransport s_defaultTransport;

        /// <summary>
        /// The transport new sources use when none is passed. Defaults to <see cref="UnityWebRequestTransport"/>;
        /// the BestHTTP adapter overrides it at startup when present.
        /// </summary>
        public static IDownloadTransport DefaultTransport
        {
            get => s_defaultTransport ?? (s_defaultTransport = new UnityWebRequestTransport());
            set => s_defaultTransport = value;
        }

        /// <summary>
        /// Fetches the bootstrap catalog (StreamingAssets by default), builds a
        /// <see cref="RemoteBundleAssetSource"/> over it and registers it ahead of the Resources fallback.
        /// </summary>
        /// <param name="remoteBaseUrl">CDN origin for Remote bundles.</param>
        /// <param name="catalogUrl">Catalog location; null = the catalog shipped in StreamingAssets.</param>
        /// <param name="cacheDirectory">Disk cache for provisioned bundles; null = the default under persistentDataPath.</param>
        /// <param name="transport">Download transport; null = <see cref="DefaultTransport"/>.</param>
        /// <param name="localBaseUrl">Origin for Local bundles; null = the StreamingAssets content URL.</param>
        /// <param name="hasher">Content hasher; null = xxHash3. MUST match the hasher the catalog was built with.</param>
        public static async UniTask<RemoteBundleAssetSource> InitializeAsync(
            string remoteBaseUrl,
            string catalogUrl = null,
            string cacheDirectory = null,
            IDownloadTransport transport = null,
            string localBaseUrl = null,
            IContentHasher hasher = null)
        {
            transport = transport ?? DefaultTransport;
            cacheDirectory = cacheDirectory ?? ContentDeliveryPaths.DefaultCacheDirectory;

            // Catalog: an explicit URL is fetched + decoded (any PFound form); otherwise the SINGLE embedded catalog
            // (AssetBundles/<platform>, pointer-named .lzma) — the same source the fail-soft path uses. There is no
            // separate PFoundContent/catalog.json anymore (the runner is the one catalog-file producer).
            Catalog catalog;
            if (catalogUrl != null)
            {
                byte[] bytes = await transport.DownloadBytesAsync(catalogUrl).AsUniTask();
                catalog = CatalogCodec.Decode(bytes, b => CatalogJson.Parse(Encoding.UTF8.GetString(b)));
            }
            else
            {
                var embedded = await EmbeddedCatalogReader.TryReadEmbeddedCatalogAsync();
                if (!embedded.Found)
                    throw new ContentDeliveryException("No embedded catalog found — run App Build to stage the content package.");
                catalog = embedded.Catalog;
            }

            var source = new RemoteBundleAssetSource(catalog, transport, cacheDirectory, remoteBaseUrl, localBaseUrl, hasher);
            AssetManager.RegisterSource(source);

            // Engine-lifecycle wiring: the frame pump for deferred unload + the Application.lowMemory hook, and
            // late-binding of bundle-packed SpriteAtlases (Include-in-Build off). Both are idempotent.
            ContentDeliveryRuntime.Install();
            SpriteAtlasBinder.Enable();
            return source;
        }

        /// <summary>
        /// Same as <see cref="InitializeAsync(string,string,string,IDownloadTransport,string,IContentHasher)"/>, but
        /// the Remote origin is taken from <paramref name="environments"/>' active environment (dev / staging / prod).
        /// Content is environment-agnostic — only the CDN origin differs — so nothing else about the wiring changes.
        /// </summary>
        public static UniTask<RemoteBundleAssetSource> InitializeAsync(
            ContentEnvironments environments,
            string catalogUrl = null,
            string cacheDirectory = null,
            IDownloadTransport transport = null,
            string localBaseUrl = null,
            IContentHasher hasher = null)
        {
            if (environments == null) throw new ArgumentNullException(nameof(environments));
            return InitializeAsync(
                environments.ResolveRemoteBaseUrl(), catalogUrl, cacheDirectory, transport, localBaseUrl, hasher);
        }

        /// <summary>
        /// Async, fail-soft boot with catalog fallback: resolves the catalog named by <paramref name="remoteConfig"/>
        /// the cheapest way — embedded if it matches, else cached, else downloaded (via
        /// <see cref="RemoteCatalogResolver.ResolveAsync"/>) — and on success registers a
        /// <see cref="RemoteBundleAssetSource"/> over it and installs the engine-lifecycle wiring. On a network/IO
        /// failure it returns an unsuccessful result carrying the message instead of throwing, so a consumer never
        /// has to touch the dormant sync <see cref="ContentCatalogService"/>.
        /// </summary>
        public static async UniTask<ContentDeliveryInitResult> InitializeWithFallbackAsync(
            RemoteContentConfig remoteConfig, ContentServiceOptions options = null, CancellationToken cancellationToken = default)
        {
            options = options ?? new ContentServiceOptions();

            var result = await RemoteCatalogResolver.ResolveAsync(remoteConfig, options, cancellationToken);
            if (!result.Success)
                return new ContentDeliveryInitResult(false, null, result.Source, result.Error);

            // Resolve the wiring exactly as the resolver's Normalize does, so the source uses the same transport,
            // cache root and hasher the catalog was acquired with.
            IDownloadTransport transport = options.Transport ?? DefaultTransport;
            string platform = options.PlatformFolder ?? ContentPlatform.ActivePlatformFolder();
            string cacheDirectory = options.CacheDirectory ?? ContentPlatform.GetRemoteAssetBundlePath(platform);
            IContentHasher hasher = options.Hasher ?? new XxHash3ContentHasher();

            var source = new RemoteBundleAssetSource(
                result.Catalog, transport, cacheDirectory, remoteConfig.OriginUrl, localBaseUrl: null, hasher);
            AssetManager.RegisterSource(source);

            // Engine-lifecycle wiring: the frame pump for deferred unload + the Application.lowMemory hook, and
            // late-binding of bundle-packed SpriteAtlases. Both are idempotent.
            ContentDeliveryRuntime.Install();
            SpriteAtlasBinder.Enable();

            return new ContentDeliveryInitResult(true, source, result.Source, null);
        }
    }
}
