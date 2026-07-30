using System;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Computes the content hash that names a bundle (remote object name == cache file name) and verifies
    /// downloaded bytes. Pluggable so the engine layer can swap the managed default for a faster on-device
    /// implementation (xxHash3 / Burst) while the pure-C# core — and its mono tests — keep a dependency-free
    /// default. Producer (build) and consumer (runtime verify) MUST use the same hasher for a given catalog.
    /// </summary>
    public interface IContentHasher
    {
        string Compute(byte[] data);
        string ComputeFile(string path);
        bool Verify(byte[] data, string expectedHash);
    }

    /// <summary>
    /// Dependency-free <see cref="IContentHasher"/> over <see cref="ContentHash"/> (SHA-256). The default in the
    /// pure core so <c>mono</c>/<c>csc</c> tests run without Unity; the engine layer overrides with xxHash3.
    /// Integrity, not security — any stable digest works as a content address.
    /// </summary>
    public sealed class Sha256ContentHasher : IContentHasher
    {
        public string Compute(byte[] data) => ContentHash.Compute(data);
        public string ComputeFile(string path) => ContentHash.ComputeFile(path);
        public bool Verify(byte[] data, string expectedHash) => ContentHash.Verify(data, expectedHash);
    }
}
