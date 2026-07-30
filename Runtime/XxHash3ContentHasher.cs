using System;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery
{
    /// <summary>
    /// <see cref="IContentHasher"/> backed by <see cref="Unity.Collections.xxHash3"/> (Burst-SIMD). The default
    /// hasher for the engine layer: content integrity here is not a security boundary (a kids-app CDN), so a fast
    /// non-cryptographic 64-bit digest is the right trade — far cheaper than SHA-256 when verifying large bundles
    /// on-device. The build pipeline names bundles with this same hasher so producer and consumer agree.
    /// The digest is the two 32-bit halves of the 64-bit hash, lowercase hex (16 chars).
    /// </summary>
    public sealed class XxHash3ContentHasher : IContentHasher
    {
        public string Compute(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return Hash(data);
        }

        public string ComputeFile(string path) => Hash(File.ReadAllBytes(path));

        public bool Verify(byte[] data, string expectedHash) =>
            string.Equals(Compute(data), expectedHash, StringComparison.OrdinalIgnoreCase);

        private static unsafe string Hash(byte[] data)
        {
            // xxHash3 takes a pointer + length; pin the array (use a 1-byte scratch for the empty case so the
            // fixed pointer is valid — length 0 hashes nothing regardless).
            byte[] pinTarget = data.Length == 0 ? s_empty : data;
            fixed (byte* p = pinTarget)
            {
                uint2 h = xxHash3.Hash64(p, data.Length);
                return h.x.ToString("x8") + h.y.ToString("x8");
            }
        }

        private static readonly byte[] s_empty = new byte[1];
    }
}
