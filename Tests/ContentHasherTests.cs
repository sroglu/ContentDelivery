using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// The xxHash3 content hasher (engine default): a content address must be deterministic, self-consistent
    /// between byte and file forms, and verify correctly — those are the only properties content-addressing
    /// relies on (it is integrity, not security, so the digest need not be cryptographic).
    /// </summary>
    public sealed class ContentHasherTests
    {
        private readonly IContentHasher _hasher = new XxHash3ContentHasher();

        [Test]
        public void Hash_IsDeterministic_AndDistinguishesContent()
        {
            var a = Encoding.UTF8.GetBytes("bundle-bytes-A");
            var b = Encoding.UTF8.GetBytes("bundle-bytes-B");

            Assert.AreEqual(_hasher.Compute(a), _hasher.Compute(a), "same bytes must hash identically");
            Assert.AreNotEqual(_hasher.Compute(a), _hasher.Compute(b), "different bytes must hash differently");
            Assert.AreEqual(16, _hasher.Compute(a).Length, "64-bit digest is 16 lowercase hex chars");
        }

        [Test]
        public void Verify_MatchesComputed_AndRejectsWrong()
        {
            var data = Encoding.UTF8.GetBytes("verify-me");
            string hash = _hasher.Compute(data);
            Assert.IsTrue(_hasher.Verify(data, hash));
            Assert.IsFalse(_hasher.Verify(data, _hasher.Compute(Encoding.UTF8.GetBytes("other"))));
        }

        [Test]
        public void ComputeFile_EqualsComputeBytes()
        {
            var data = Encoding.UTF8.GetBytes("file-and-bytes-agree");
            string path = Path.Combine(Application.temporaryCachePath, "pf_cd_hash_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(path, data);
                Assert.AreEqual(_hasher.Compute(data), _hasher.ComputeFile(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void EmptyInput_HashesWithoutThrowing()
        {
            Assert.AreEqual(16, _hasher.Compute(Array.Empty<byte>()).Length);
        }
    }
}
