using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery
{
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
            catalogUrl = catalogUrl ??
                ContentDeliveryPaths.StreamingAssetsContentUrl + "/" + ContentDeliveryPaths.CatalogFileName;
            cacheDirectory = cacheDirectory ?? ContentDeliveryPaths.DefaultCacheDirectory;

            byte[] bytes = await transport.DownloadBytesAsync(catalogUrl).AsUniTask();
            var catalog = CatalogJson.Parse(Encoding.UTF8.GetString(bytes));

            var source = new RemoteBundleAssetSource(catalog, transport, cacheDirectory, remoteBaseUrl, localBaseUrl, hasher);
            AssetManager.RegisterSource(source);
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
    }
}
