using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Typed content-delivery failures, split out of the single <see cref="ContentDeliveryException"/> so callers
    /// (and the retry loop) can distinguish transient from fatal. Each sets <see cref="ContentDeliveryException.Retryable"/>
    /// according to whether re-attempting could plausibly succeed.
    /// </summary>

    /// <summary>Transient transport failure (timeout, connection reset, 5xx) — worth retrying.</summary>
    public sealed class NetworkException : ContentDeliveryException
    {
        public NetworkException(string message, Exception inner = null) : base(message, inner, retryable: true) { }
    }

    /// <summary>Downloaded bytes did not match the expected content hash — retryable (a re-fetch may get good bytes).</summary>
    public sealed class HashMismatchException : ContentDeliveryException
    {
        public string BundleName { get; }
        public HashMismatchException(string bundleName)
            : base("Hash mismatch for bundle '" + bundleName + "'.", retryable: true) { BundleName = bundleName; }
    }

    /// <summary>The device lacks disk capacity to cache the content — fatal, not retryable.</summary>
    public sealed class NotEnoughDiskCapacityException : ContentDeliveryException
    {
        public NotEnoughDiskCapacityException(string message, Exception inner = null) : base(message, inner, retryable: false) { }
    }

    /// <summary>A bundle's bytes could not be decompressed (corrupt / wrong codec) — fatal, not retryable.</summary>
    public sealed class DecompressionFailedException : ContentDeliveryException
    {
        public DecompressionFailedException(string message, Exception inner = null) : base(message, inner, retryable: false) { }
    }

    /// <summary>Retries were exhausted; carries the attempt count and the last underlying cause — fatal.</summary>
    public sealed class RetryCountExceededException : ContentDeliveryException
    {
        public int Attempts { get; }
        public RetryCountExceededException(string bundleName, int attempts, Exception inner)
            : base("Failed to provision bundle '" + bundleName + "' after " + attempts + " attempt(s).", inner, retryable: false)
        {
            Attempts = attempts;
        }
    }
}
