using System;
using System.Threading;
using System.Threading.Tasks;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Thin HTTP fetch boundary. Retry/backoff, hash verification and caching live ABOVE this (in
    /// <see cref="BundleProvisioner"/>), so transports stay dumb and swappable: BestHTTP,
    /// UnityWebRequest, or a fake for tests.
    /// </summary>
    public interface IDownloadTransport
    {
        Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default);
        Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Base for content-delivery failures. <see cref="Retryable"/> classifies whether retrying the operation
    /// could plausibly succeed (transient network / bad bytes) versus a fatal condition (no disk, corrupt
    /// archive, retries exhausted). See the typed subclasses in <c>ContentDeliveryExceptions.cs</c>.
    /// </summary>
    public class ContentDeliveryException : Exception
    {
        /// <summary>True if retrying the failed operation might succeed; false for fatal/terminal failures.</summary>
        public bool Retryable { get; }

        public ContentDeliveryException(string message, bool retryable = false) : base(message) { Retryable = retryable; }
        public ContentDeliveryException(string message, Exception inner, bool retryable = false) : base(message, inner) { Retryable = retryable; }
    }
}
