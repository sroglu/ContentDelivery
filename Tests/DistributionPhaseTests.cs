using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// Covers the distribution-mode origin routing (G2) and phased preload (G1). Bundles here are arbitrary
    /// content-addressed byte payloads, not real AssetBundles — both features operate purely at the
    /// provision (download → verify → cache) layer, below any AssetBundle load, so no bundle build is needed.
    /// Origins are local directories served over file://, exactly as a CDN / StreamingAssets would.
    /// </summary>
    public sealed class DistributionPhaseTests
    {
        private string _root;
        private string _remote;   // CDN stand-in
        private string _local;    // StreamingAssets stand-in
        private string _cache;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Application.temporaryCachePath, "pf_cd_dist_" + Guid.NewGuid().ToString("N"));
            _remote = Path.Combine(_root, "remote");
            _local = Path.Combine(_root, "local");
            _cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(_remote);
            Directory.CreateDirectory(_local);
            Directory.CreateDirectory(_cache);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static string Url(string dir) => new Uri(dir).AbsoluteUri;

        private static string Publish(string originDir, byte[] bytes)
        {
            string hash = ContentHash.Compute(bytes);
            File.WriteAllBytes(Path.Combine(originDir, hash), bytes); // content-addressed object
            return hash;
        }

        [UnityTest]
        public IEnumerator Provisioner_RoutesLocalBundleToLocalOrigin() => UniTask.ToCoroutine(async () =>
        {
            // Content lives ONLY at the local (StreamingAssets) origin; the remote origin stays empty.
            var bytes = Encoding.UTF8.GetBytes("local-shipped-content");
            string hash = Publish(_local, bytes);
            var localBundle = new CatalogBundle { Name = "boot", Hash = hash, Local = true };

            var provisioner = new BundleProvisioner(
                new UnityWebRequestTransport(), _cache, baseUrl: Url(_remote), localBaseUrl: Url(_local), maxAttempts: 1);

            string path = await provisioner.EnsureBundleAsync(localBundle);
            Assert.IsTrue(File.Exists(path), "a Local bundle must provision from the local origin even with an empty CDN origin");
            Assert.AreEqual(bytes, File.ReadAllBytes(path));
        });

        [UnityTest]
        public IEnumerator Provisioner_RoutesRemoteBundleToRemoteOrigin() => UniTask.ToCoroutine(async () =>
        {
            // Content lives ONLY at the remote (CDN) origin; the local origin stays empty.
            var bytes = Encoding.UTF8.GetBytes("remote-cdn-content");
            string hash = Publish(_remote, bytes);
            var remoteBundle = new CatalogBundle { Name = "level", Hash = hash, Local = false };

            var provisioner = new BundleProvisioner(
                new UnityWebRequestTransport(), _cache, baseUrl: Url(_remote), localBaseUrl: Url(_local), maxAttempts: 1);

            string path = await provisioner.EnsureBundleAsync(remoteBundle);
            Assert.IsTrue(File.Exists(path), "a Remote bundle must provision from the CDN origin");
            Assert.AreEqual(bytes, File.ReadAllBytes(path));
        });

        [UnityTest]
        public IEnumerator Preload_ProvisionsOnlyUpToPhase_AndWalksDependencies() => UniTask.ToCoroutine(async () =>
        {
            // shared ← essential (Essential), standard (Standard). Preloading Essential must bring down the
            // essential bundle AND its shared dependency, but NOT the standard-phase bundle.
            byte[] sharedBytes = Encoding.UTF8.GetBytes("shared-dep");
            byte[] essBytes = Encoding.UTF8.GetBytes("boot-content");
            byte[] stdBytes = Encoding.UTF8.GetBytes("world-content");
            string sharedHash = Publish(_remote, sharedBytes);
            string essHash = Publish(_remote, essBytes);
            string stdHash = Publish(_remote, stdBytes);

            var catalog = new Catalog(
                new[]
                {
                    new CatalogBundle { Name = "shared", Hash = sharedHash, Dependencies = Array.Empty<string>() },
                    new CatalogBundle { Name = "essential", Hash = essHash, Dependencies = new[] { "shared" } },
                    new CatalogBundle { Name = "standard", Hash = stdHash, Dependencies = Array.Empty<string>() },
                },
                new[]
                {
                    new CatalogAsset { Address = "boot/ui", Bundle = "essential", AssetName = "boot/ui", Phase = (int)AssetPhase.Essential },
                    new CatalogAsset { Address = "world/level", Bundle = "standard", AssetName = "world/level", Phase = (int)AssetPhase.Standard },
                });

            // Payloads here are published via the SHA-256 ContentHash helper, so pin the source to SHA-256
            // (the app default is xxHash3; the build pipeline names with the same hasher the source verifies with).
            var source = new RemoteBundleAssetSource(
                catalog, new UnityWebRequestTransport(), _cache, Url(_remote), Url(_local), new Sha256ContentHasher());

            await source.PreloadAsync(AssetPhase.Essential);
            Assert.IsTrue(File.Exists(Path.Combine(_cache, essHash)), "essential bundle should be provisioned");
            Assert.IsTrue(File.Exists(Path.Combine(_cache, sharedHash)), "essential's dependency should be provisioned too");
            Assert.IsFalse(File.Exists(Path.Combine(_cache, stdHash)), "standard-phase content must NOT be provisioned by an Essential preload");

            await source.PreloadAsync(AssetPhase.Standard);
            Assert.IsTrue(File.Exists(Path.Combine(_cache, stdHash)), "standard content provisions once its phase is preloaded");
        });

        [UnityTest]
        public IEnumerator PreloadSequential_EssentialCompletesBeforeStandardBegins() => UniTask.ToCoroutine(async () =>
        {
            // essential ← shared (Essential phase); standard (Standard phase). A sequential preload must finish
            // every Essential-phase download before it starts the Standard-phase one.
            byte[] sharedBytes = Encoding.UTF8.GetBytes("shared-dep-seq");
            byte[] essBytes = Encoding.UTF8.GetBytes("boot-content-seq");
            byte[] stdBytes = Encoding.UTF8.GetBytes("world-content-seq");
            string sharedHash = Publish(_remote, sharedBytes);
            string essHash = Publish(_remote, essBytes);
            string stdHash = Publish(_remote, stdBytes);

            var catalog = new Catalog(
                new[]
                {
                    new CatalogBundle { Name = "shared", Hash = sharedHash, Dependencies = Array.Empty<string>() },
                    new CatalogBundle { Name = "essential", Hash = essHash, Dependencies = new[] { "shared" } },
                    new CatalogBundle { Name = "standard", Hash = stdHash, Dependencies = Array.Empty<string>() },
                },
                new[]
                {
                    new CatalogAsset { Address = "boot/ui", Bundle = "essential", AssetName = "boot/ui", Phase = (int)AssetPhase.Essential },
                    new CatalogAsset { Address = "world/level", Bundle = "standard", AssetName = "world/level", Phase = (int)AssetPhase.Standard },
                });

            var recorder = new RecordingTransport(new UnityWebRequestTransport());
            var source = new RemoteBundleAssetSource(
                catalog, recorder, _cache, Url(_remote), Url(_local), new Sha256ContentHasher());

            await source.PreloadPhasesSequentialAsync(AssetPhase.Standard);

            int essIndex = recorder.IndexOfHash(essHash);
            int sharedIndex = recorder.IndexOfHash(sharedHash);
            int stdIndex = recorder.IndexOfHash(stdHash);

            Assert.GreaterOrEqual(essIndex, 0, "essential bundle was fetched");
            Assert.GreaterOrEqual(sharedIndex, 0, "shared dependency was fetched");
            Assert.GreaterOrEqual(stdIndex, 0, "standard bundle was fetched");
            Assert.Greater(stdIndex, essIndex, "Standard must be fetched AFTER the Essential bundle");
            Assert.Greater(stdIndex, sharedIndex, "Standard must be fetched AFTER Essential's dependency");
        });

        /// <summary>Wraps a transport and records the order of fetches, so a test can assert provisioning sequence.</summary>
        private sealed class RecordingTransport : IDownloadTransport
        {
            private readonly IDownloadTransport _inner;
            public readonly List<string> Fetches = new List<string>(); // URLs, in call order

            public RecordingTransport(IDownloadTransport inner) { _inner = inner; }

            public Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
            {
                Fetches.Add(url);
                return _inner.DownloadBytesAsync(url, cancellationToken);
            }

            public Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
            {
                Fetches.Add(url);
                return _inner.DownloadToFileAsync(url, destinationPath, cancellationToken);
            }

            /// <summary>Index of the first fetch whose URL ends with <paramref name="hash"/> (content-addressed name), or -1.</summary>
            public int IndexOfHash(string hash)
            {
                for (int i = 0; i < Fetches.Count; i++)
                    if (Fetches[i].EndsWith(hash, StringComparison.Ordinal)) return i;
                return -1;
            }
        }
    }
}
