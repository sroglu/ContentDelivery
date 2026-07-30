#if PFOUND_BESTHTTP
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BestHTTP;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Transport
{
    /// <summary>
    /// <see cref="IDownloadTransport"/> backed by the BestHTTP library (HTTP/2, connection reuse). Lives
    /// in its own asmdef gated behind the <c>PFOUND_BESTHTTP</c> scripting define so the foundation
    /// compiles without BestHTTP present; enable the define in projects that ship the library. Stays dumb:
    /// retry/backoff, hash verification and caching live above it in <see cref="BundleProvisioner"/>.
    /// <c>GetRawDataAsync</c> already surfaces server errors as exceptions and honors cancellation.
    /// </summary>
    public sealed class BestHttpTransport : IDownloadTransport
    {
        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            var request = new HTTPRequest(new Uri(url));
            return await request.GetRawDataAsync(cancellationToken);
        }

        public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            byte[] bytes = await DownloadBytesAsync(url, cancellationToken);

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Publish atomically so a failed/cancelled download never leaves a partial file at the path.
            string temp = destinationPath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(temp, destinationPath);
        }
    }
}
#endif
