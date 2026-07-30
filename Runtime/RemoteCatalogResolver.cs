using System;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>Outcome of a remote→cache→embedded catalog resolution.</summary>
    public readonly struct CatalogResolveResult
    {
        public readonly bool Success;
        public readonly Catalog Catalog;      // null on failure
        public readonly CatalogSource Source; // which branch produced it: Embedded | Cached | Download
        public readonly string Error;         // null on success

        public CatalogResolveResult(bool success, Catalog catalog, CatalogSource source, string error)
        {
            Success = success;
            Catalog = catalog;
            Source = source;
            Error = error;
        }
    }

    /// <summary>
    /// Acquires the catalog named by a <see cref="RemoteContentConfig"/> the cheapest way — the embedded copy if it
    /// already matches, else a cached copy, else a download — and decodes it. Fail-soft: returns a typed
    /// <see cref="CatalogResolveResult"/> (carrying the error message) instead of throwing on network/IO failure;
    /// only <see cref="OperationCanceledException"/> propagates. Does NOT initialize <see cref="ContentCatalogService"/>
    /// — it resolves the catalog only, so the async runtime (<see cref="ContentDeliveryBootstrap"/>) and the dormant
    /// sync service can share one acquisition/fallback path.
    /// </summary>
    public static class RemoteCatalogResolver
    {
        /// <summary>
        /// Resolves the catalog: reads the embedded copy, checks the cache, decides the cheapest source via
        /// <see cref="CatalogAcquisitionPlan.Decide"/>, and returns the decoded <see cref="Catalog"/> together with
        /// the branch it came from. On a network/IO failure it returns an unsuccessful result carrying the message.
        /// </summary>
        public static async UniTask<CatalogResolveResult> ResolveAsync(
            RemoteContentConfig remoteConfig, ContentServiceOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var opts = Normalize(options);

            // Pointer-driven discovery: ask the remote pointer which catalog is current, so the CDN can serve the
            // arbitrary (hash-stamped) name the config can't predict. Fail-soft — an offline / missing / unparsable
            // pointer falls back to the config-carried CatalogFileName (legacy CDN / not-yet-published).
            string required = remoteConfig.CatalogFileName;
            if (!remoteConfig.IsOffline)
            {
                var pointer = await new RemoteCatalogPointerReader(opts.Transport)
                    .TryReadPointerAsync(remoteConfig.OriginUrl, remoteConfig.PlatformFolder, cancellationToken);
                if (pointer.Resolved) required = pointer.CatalogFileName;
            }

            var embedded = await EmbeddedCatalogReader.TryReadEmbeddedCatalogAsync(opts.PlatformFolder);
            string cachedPath = Path.Combine(opts.CacheDirectory, required);
            bool cached = File.Exists(cachedPath);

            var plan = CatalogAcquisitionPlan.Decide(required, embedded.Found ? embedded.FileName : null, cached);

            // Downgrade guard: the pointer can name a catalog OLDER than the build-embedded one (rollback /
            // stale CDN pointer). The (appVersion, build) postfix in the name orders them — if the pointer's
            // target is older than embedded, keep embedded instead of pulling the older content.
            if (plan != CatalogSource.Embedded && embedded.Found)
            {
                var requiredVersion = CatalogNameVersion.Parse(required);
                var embeddedVersion = CatalogNameVersion.Parse(embedded.FileName);
                if (requiredVersion.Parsed && embeddedVersion.Parsed && requiredVersion.CompareTo(embeddedVersion) < 0)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[ContentDelivery] Downgrade blocked: pointer catalog '{required}' ({requiredVersion}) is older " +
                        $"than embedded '{embedded.FileName}' ({embeddedVersion}) — keeping the embedded catalog.");
                    return new CatalogResolveResult(true, embedded.Catalog, CatalogSource.Embedded, null);
                }
            }

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
                        // Fetch the pointer-discovered catalog (NOT remoteConfig.GetCatalogUrl(), whose name is the
                        // config-predicted one) from the same platform folder.
                        string catalogUrl = AssetBundleLayout.CombineUrl(remoteConfig.ResolveContentUrl(), required);
                        byte[] bytes = await opts.Transport.DownloadBytesAsync(catalogUrl, cancellationToken);
                        Directory.CreateDirectory(opts.CacheDirectory);
                        File.WriteAllBytes(cachedPath, bytes);
                        catalog = DecodeCatalogBytes(bytes);
                        break;
                }

                return new CatalogResolveResult(true, catalog, plan, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return new CatalogResolveResult(false, null, plan, e.Message);
            }
        }

        /// <summary>
        /// Fills the optional slots of <paramref name="o"/> with the ContentDelivery defaults: the default transport,
        /// xxHash3, the active platform folder and the downloaded-bundle cache root. Shared by the async runtime and
        /// the sync service so both resolve wiring identically.
        /// </summary>
        internal static ContentServiceOptions Normalize(ContentServiceOptions o)
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

        // Decodes catalog bytes: the codec unwraps any container framing, then the JSON body is parsed.
        internal static Catalog DecodeCatalogBytes(byte[] bytes) =>
            CatalogCodec.Decode(bytes, b => CatalogJson.Parse(Encoding.UTF8.GetString(b)));
    }
}
