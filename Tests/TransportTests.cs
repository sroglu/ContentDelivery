using System;
using System.Collections;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery.Tests
{
    public sealed class TransportTests
    {
        private string _root;
        private string _origin;
        private string _cache;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Application.temporaryCachePath, "pf_cd_transport_" + Guid.NewGuid().ToString("N"));
            _origin = Path.Combine(_root, "origin");
            _cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(_origin);
            Directory.CreateDirectory(_cache);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string OriginUrl() => new Uri(_origin).AbsoluteUri;

        private string PublishBytes(byte[] bytes)
        {
            string hash = ContentHash.Compute(bytes);
            File.WriteAllBytes(Path.Combine(_origin, hash), bytes); // content-addressed remote object
            return hash;
        }

        [UnityTest]
        public IEnumerator UnityWebRequest_DownloadsBytesOverFileUrl() => UniTask.ToCoroutine(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("payload-bytes");
            string hash = PublishBytes(bytes);

            var transport = new UnityWebRequestTransport();
            var got = await transport.DownloadBytesAsync(OriginUrl() + "/" + hash);
            Assert.AreEqual(bytes, got);
        });

        [UnityTest]
        public IEnumerator Provisioner_DownloadsVerifiesCaches_ThenHitsCache() => UniTask.ToCoroutine(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("bundle-content-v1");
            string hash = PublishBytes(bytes);
            var bundle = new CatalogBundle { Name = "b", Hash = hash };

            var provisioner = new BundleProvisioner(new UnityWebRequestTransport(), _cache, OriginUrl());

            string path = await provisioner.EnsureBundleAsync(bundle);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(bytes, File.ReadAllBytes(path));

            // Remove the remote object: a cache hit must serve offline with no download.
            File.Delete(Path.Combine(_origin, hash));
            string again = await provisioner.EnsureBundleAsync(bundle);
            Assert.AreEqual(path, again);
            Assert.AreEqual(bytes, File.ReadAllBytes(again));
        });

        [UnityTest]
        public IEnumerator Provisioner_RejectsHashMismatch() => UniTask.ToCoroutine(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("real-content");
            // Claim a hash that does not match the bytes served at that name.
            string wrongHash = ContentHash.Compute(Encoding.UTF8.GetBytes("something-else"));
            File.WriteAllBytes(Path.Combine(_origin, wrongHash), bytes);

            var bundle = new CatalogBundle { Name = "b", Hash = wrongHash };
            var provisioner = new BundleProvisioner(new UnityWebRequestTransport(), _cache, OriginUrl(), maxAttempts: 1);

            bool threw = false;
            try { await provisioner.EnsureBundleAsync(bundle); }
            catch (ContentDeliveryException) { threw = true; }
            Assert.IsTrue(threw, "a hash mismatch must be rejected, not cached");
            Assert.IsFalse(File.Exists(Path.Combine(_cache, wrongHash)), "rejected bytes must not be cached");
        });
    }
}
