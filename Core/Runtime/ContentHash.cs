using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PFound.ContentDelivery.Core
{
    /// <summary>SHA-256 content hashing for bundle integrity (the hash is also the cache/remote name).</summary>
    public static class ContentHash
    {
        public static string Compute(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        public static string ComputeFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return ToHex(sha.ComputeHash(stream));
        }

        public static bool Verify(byte[] data, string expectedHash) =>
            string.Equals(Compute(data), expectedHash, StringComparison.OrdinalIgnoreCase);

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
