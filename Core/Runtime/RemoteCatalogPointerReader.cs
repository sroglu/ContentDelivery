using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ContentDelivery.Core
{
    /// <summary>What reading the remote pointer file yielded: whether it named a catalog, and if so which one.</summary>
    public readonly struct RemoteCatalogPointer
    {
        /// <summary>True when the pointer file was fetched and it named a catalog file.</summary>
        public readonly bool Resolved;

        /// <summary>The catalog file name (with extension) the pointer advertises; null when unresolved.</summary>
        public readonly string CatalogFileName;

        public RemoteCatalogPointer(bool resolved, string catalogFileName)
        {
            Resolved = resolved;
            CatalogFileName = catalogFileName;
        }

        public static readonly RemoteCatalogPointer NotFound = new RemoteCatalogPointer(false, null);
    }

    /// <summary>
    /// Learns the current remote catalog file name without listing any directory: it fetches one fixed pointer file
    /// (<see cref="AssetBundleLayout.RemoteCatalogPointerFileName"/>) from the platform folder and reads the
    /// <see cref="AssetBundleLayout.CatalogFileNameKey"/> entry out of it. That is how the app discovers which
    /// versioned catalog to pull. Fail-soft: an offline, missing or unparsable pointer maps to
    /// <see cref="RemoteCatalogPointer.NotFound"/> and only a cancellation is allowed to surface. Pure Core over an
    /// injected <see cref="IDownloadTransport"/>.
    /// </summary>
    public sealed class RemoteCatalogPointerReader
    {
        private readonly IDownloadTransport _transport;

        public RemoteCatalogPointerReader(IDownloadTransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            _transport = transport;
        }

        public async Task<RemoteCatalogPointer> TryReadPointerAsync(
            string originUrl, string platformFolder, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(originUrl)) return RemoteCatalogPointer.NotFound; // offline

            string url = AssetBundleLayout.CombineUrl(
                AssetBundleLayout.CombineUrl(originUrl, AssetBundleLayout.PlatformSubPath(platformFolder)),
                AssetBundleLayout.RemoteCatalogPointerFileName);

            byte[] bytes;
            try
            {
                bytes = await _transport.DownloadBytesAsync(url, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // No reachable pointer just means "no remote catalog advertised yet" — a normal state, not a failure.
                return RemoteCatalogPointer.NotFound;
            }

            return TryParseCatalogFileName(Encoding.UTF8.GetString(bytes), out string name)
                ? new RemoteCatalogPointer(true, name)
                : RemoteCatalogPointer.NotFound;
        }

        /// <summary>
        /// Reads the <see cref="AssetBundleLayout.CatalogFileNameKey"/> value out of a line-oriented key=value pointer
        /// document (blank lines and <c>#</c>/<c>!</c> comment lines skipped; the first <c>=</c> or <c>:</c> divides key
        /// from value). Pure — the network-free half of the fetch.
        /// </summary>
        public static bool TryParseCatalogFileName(string pointerText, out string catalogFileName)
        {
            catalogFileName = null;
            if (string.IsNullOrEmpty(pointerText)) return false;

            foreach (var rawLine in pointerText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;

                int divider = FindDivider(line);
                if (divider <= 0) continue;

                string key = line.Substring(0, divider).Trim();
                if (!string.Equals(key, AssetBundleLayout.CatalogFileNameKey, StringComparison.Ordinal)) continue;

                string value = line.Substring(divider + 1).Trim();
                if (value.Length == 0) return false;
                catalogFileName = value;
                return true;
            }
            return false;
        }

        private static int FindDivider(string line)
        {
            for (int i = 0; i < line.Length; i++)
                if (line[i] == '=' || line[i] == ':') return i;
            return -1;
        }
    }
}
