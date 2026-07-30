using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Transport
{
    /// <summary>
    /// Dependency-free <see cref="IDownloadTransport"/> over <see cref="UnityWebRequest"/>. The default
    /// transport and the supported path on WebGL (where BestHTTP is limited). Stays dumb: retry/backoff,
    /// hash verification and caching live above it in <see cref="BundleProvisioner"/>. Main-thread only
    /// (UnityWebRequest must be issued from the main thread).
    /// </summary>
    public sealed class UnityWebRequestTransport : IDownloadTransport
    {
        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);
                if (request.result != UnityWebRequest.Result.Success)
                    throw new NetworkException("GET '" + url + "' failed: " + request.error);
                return request.downloadHandler.data;
            }
        }

        public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Stream straight to a temp file, then publish atomically so a cancelled/failed download
            // never leaves a partial file at the real path.
            string temp = destinationPath + ".tmp";
            using (var request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerFile(temp) { removeFileOnAbort = true };
                await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (File.Exists(temp)) File.Delete(temp);
                    throw new NetworkException("GET '" + url + "' -> file failed: " + request.error);
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(temp, destinationPath);
        }
    }
}
