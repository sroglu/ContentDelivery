using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PFound.ContentDelivery.Core;
using PFound.Compression;

namespace PFound.ContentDelivery.Core.Tests
{
    /// <summary>
    /// Standalone mono/csc runner for the pure-C# delivery core: catalog resolution + the
    /// download/verify/cache/retry orchestration, driven by an in-memory catalog, a FakeTransport,
    /// and a temp cache directory — no Unity, no network.
    /// </summary>
    internal static class Program
    {
        private static int s_passed;
        private static int s_failed;

        private static int Main()
        {
            var temp = Path.Combine(Path.GetTempPath(), "pf_content_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                Run("Catalog resolves address to asset and bundle", () => Catalog_Resolve());
                Run("Catalog AssetsWithLabel returns only tagged assets", () => Catalog_AssetsWithLabel());
                Run("GetPackClosure unions members deps-first + dedups + throws on unknown", () => Catalog_PackClosure());
                Run("ContentEnvironments resolves active + by-name origin and fails fast", () => Environments_Resolve());
                Run("PhasesUpTo is distinct ascending + AssetsInPhase slices exactly", () => Catalog_PhaseSlicing());
                Run("GetBundleClosure orders dependencies first", () => Closure_DepsFirst());
                Run("GetBundleClosure dedups shared dependencies", () => Closure_Dedup());
                Run("GetBundleClosure throws on cycle", () => Closure_Cycle());
                Run("GetBundleClosure throws on missing bundle", () => Closure_Missing());
                Run("EnsureBundle downloads, verifies and caches", () => Ensure_DownloadsAndCaches(temp));
                Run("EnsureBundle second call is a cache hit (no download)", () => Ensure_CacheHit(temp));
                Run("EnsureBundle reuses a pre-populated cache offline", () => Ensure_OfflineCache(temp));
                Run("EnsureBundle retries then succeeds", () => Ensure_RetrySucceeds(temp));
                Run("EnsureBundle fails after max attempts", () => Ensure_FailsAfterMax(temp));
                Run("EnsureBundle rejects a hash mismatch", () => Ensure_HashMismatch(temp));
                Run("EnsureBundle trusts a present cache file by its content-addressed name", () => Ensure_TrustsCacheByName(temp));
                Run("EnsureClosure provisions every bundle deps-first", () => Ensure_Closure(temp));
                Run("EnsureBundle decompresses an LZMA bundle + caches", () => Ensure_DecompressesLzmaBundle(temp));
                Run("LZMA round-trips empty + tiny inputs", () => Lzma_RoundTripEdge());
                Run("LZMA round-trips + compresses repetitive data", () => Lzma_RoundTripCompressible());
                Run("LZMA round-trips random (incompressible) data", () => Lzma_RoundTripRandom());
                Run("LZMA streams across the sliding-window wrap (> DictSize)", () => Lzma_WrapsWindow());
                Run("LZMA pooled decoders decompress concurrently + correctly", () => Lzma_PooledConcurrent());
                Run("Scheduler provisions all bundles (balanced) + reports progress", () => Scheduler_Balanced(temp));
                Run("Scheduler provisions all bundles (sequential)", () => Scheduler_Sequential(temp));
                Run("Scheduler retries transient failures within its budget", () => Scheduler_RetriesTransient(temp));
                Run("Scheduler disk-capacity precheck rejects an impossible total", () => Scheduler_DiskPrecheck(temp));
                Run("Scheduler retries corrupt bytes under the integrity budget then fails", () => Scheduler_IntegrityRetries(temp));
                Run("Scheduler stall watchdog abandons a hung attempt then succeeds", () => Scheduler_StallWatchdog(temp));
                Run("Scheduler feeds the speed meter Good/Bad classification", () => Scheduler_FeedsSpeedMeter(temp));
                Run("SpeedMeter raises only on Good/Bad transitions (direct rate)", () => SpeedMeter_DirectTransitions());
                Run("SpeedMeter cumulative sampling honours the check interval", () => SpeedMeter_IntervalGating());
                Run("DeferredUnloadQueue releases after the frame window", () => DeferredQueue_ReleasesAfterFrames());
                Run("DeferredUnloadQueue cancel prevents a pending release", () => DeferredQueue_CancelRevives());
                Run("DeferredUnloadQueue flush releases everything at once", () => DeferredQueue_Flush());
                Run("CatalogBinary round-trips bundles, assets, deps, flags + version", () => CatalogBinary_RoundTrips());
                Run("CatalogBinary rejects non-catalog bytes", () => CatalogBinary_RejectsBadMagic());
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { /* best effort temp cleanup */ }
            }

            Console.WriteLine();
            Console.WriteLine(s_failed == 0
                ? "ALL PASSED (" + s_passed + ")"
                : (s_passed + " passed, " + s_failed + " FAILED"));
            return s_failed == 0 ? 0 : 1;
        }

        // ---- tests -------------------------------------------------------------------------------

        private static void Catalog_Resolve()
        {
            var catalog = MakeCatalog();
            Assert(catalog.TryResolveAsset("ui/hud", out var asset), "address resolves");
            AssertEqual("bundle-ui", asset.Bundle, "asset bundle");
            AssertEqual("HUD", asset.AssetName, "asset name");
            Assert(!catalog.TryResolveAsset("nope", out _), "unknown address fails");
        }

        private static void Catalog_AssetsWithLabel()
        {
            var assets = new[]
            {
                new CatalogAsset { Address = "a", Bundle = "b", Labels = new[] { "ui", "boot" } },
                new CatalogAsset { Address = "b", Bundle = "b", Labels = new[] { "ui" } },
                new CatalogAsset { Address = "c", Bundle = "b" }, // no labels (default empty)
            };
            var catalog = new Catalog(Array.Empty<CatalogBundle>(), assets);

            var ui = new List<string>();
            foreach (var a in catalog.AssetsWithLabel("ui")) ui.Add(a.Address);
            ui.Sort();
            AssertEqual(2, ui.Count, "two assets tagged ui");
            AssertEqual("a", ui[0], "ui includes a");
            AssertEqual("b", ui[1], "ui includes b");

            var boot = new List<CatalogAsset>(catalog.AssetsWithLabel("boot"));
            AssertEqual(1, boot.Count, "one asset tagged boot");
            AssertEqual("a", boot[0].Address, "boot is a");

            AssertEqual(0, new List<CatalogAsset>(catalog.AssetsWithLabel("missing")).Count, "unknown label is empty");
            AssertEqual(0, new List<CatalogAsset>(catalog.AssetsWithLabel("UI")).Count, "label match is case-sensitive");
        }

        private static void Catalog_PackClosure()
        {
            // ui → shared, world → shared : the pack {ui, world} must provision shared once, deps-first.
            var bundles = new[]
            {
                new CatalogBundle { Name = "shared", Hash = "hs" },
                new CatalogBundle { Name = "ui", Hash = "hu", Dependencies = new[] { "shared" } },
                new CatalogBundle { Name = "world", Hash = "hw", Dependencies = new[] { "shared" } },
            };
            var packs = new[]
            {
                new CatalogPack { Name = "boot", Bundles = new[] { "ui", "world" } },
            };
            var catalog = new Catalog(bundles, Array.Empty<CatalogAsset>(), null, packs);

            Assert(catalog.TryGetPack("boot", out var boot), "pack resolves");
            AssertEqual(2, boot.Bundles.Length, "pack member count");

            var closure = catalog.GetPackClosure("boot");
            var names = new List<string>();
            foreach (var b in closure) names.Add(b.Name);
            AssertEqual(3, names.Count, "shared appears once across both members");
            AssertEqual(1, names.FindAll(n => n == "shared").Count, "shared deduped");
            Assert(names.IndexOf("shared") < names.IndexOf("ui"), "shared before ui (deps-first)");
            Assert(names.IndexOf("shared") < names.IndexOf("world"), "shared before world (deps-first)");

            AssertThrows<InvalidOperationException>(() => catalog.GetPackClosure("ghost"), "unknown pack throws");
        }

        private static void Environments_Resolve()
        {
            var origins = new[]
            {
                new KeyValuePair<string, string>("dev", "https://dev.cdn/content"),
                new KeyValuePair<string, string>("staging", "https://staging.cdn/content"),
                new KeyValuePair<string, string>("prod", "https://cdn/content"),
            };
            var env = new ContentEnvironments(origins, "staging");

            AssertEqual("staging", env.Active, "active env tracked");
            AssertEqual("https://staging.cdn/content", env.ResolveRemoteBaseUrl(), "active origin resolves");
            AssertEqual("https://cdn/content", env.ResolveRemoteBaseUrl("prod"), "by-name origin resolves");
            Assert(env.TryGetOrigin("dev", out var dev) && dev == "https://dev.cdn/content", "TryGetOrigin hit");
            Assert(!env.TryGetOrigin("qa", out _), "TryGetOrigin miss");
            AssertEqual(3, new List<string>(env.Names).Count, "all environments listed");

            AssertThrows<InvalidOperationException>(() => env.ResolveRemoteBaseUrl("qa"), "unknown env throws");
            // Fail-fast: an active environment that isn't configured is rejected at construction.
            AssertThrows<ArgumentException>(
                () => new ContentEnvironments(origins, "qa"), "active-not-configured rejected at ctor");
        }

        private static void Catalog_PhaseSlicing()
        {
            var assets = new[]
            {
                new CatalogAsset { Address = "boot", Bundle = "b", Phase = 0 },     // Essential
                new CatalogAsset { Address = "hud",  Bundle = "b", Phase = 100 },   // Early
                new CatalogAsset { Address = "hud2", Bundle = "b", Phase = 100 },   // Early (same phase)
                new CatalogAsset { Address = "lvl",  Bundle = "b", Phase = 200 },   // Standard
                new CatalogAsset { Address = "cosmo", Bundle = "b", Phase = 300 },  // Deferred (above the cap)
            };
            var catalog = new Catalog(new[] { new CatalogBundle { Name = "b", Hash = "h" } }, assets);

            var phases = new List<int>(catalog.PhasesUpTo(200));
            AssertEqual(3, phases.Count, "distinct phases up to Standard (Deferred excluded)");
            AssertEqual(0, phases[0], "ascending: Essential first");
            AssertEqual(100, phases[1], "ascending: Early second");
            AssertEqual(200, phases[2], "ascending: Standard third");

            AssertEqual(2, new List<CatalogAsset>(catalog.AssetsInPhase(100)).Count, "two assets at Early exactly");
            AssertEqual(1, new List<CatalogAsset>(catalog.AssetsInPhase(0)).Count, "one asset at Essential exactly");
            AssertEqual(0, new List<CatalogAsset>(catalog.AssetsInPhase(150)).Count, "no asset at an unused phase value");
        }

        private static void Closure_DepsFirst()
        {
            var catalog = MakeCatalog();
            var closure = catalog.GetBundleClosure("bundle-ui");
            // bundle-ui depends on bundle-shared → shared must come first
            AssertEqual(2, closure.Count, "closure size");
            AssertEqual("bundle-shared", closure[0].Name, "dependency first");
            AssertEqual("bundle-ui", closure[1].Name, "dependent last");
        }

        private static void Closure_Dedup()
        {
            var bundles = new[]
            {
                new CatalogBundle { Name = "a", Hash = "ha", Dependencies = new[] { "c" } },
                new CatalogBundle { Name = "b", Hash = "hb", Dependencies = new[] { "c" } },
                new CatalogBundle { Name = "root", Hash = "hr", Dependencies = new[] { "a", "b" } },
                new CatalogBundle { Name = "c", Hash = "hc" },
            };
            var catalog = new Catalog(bundles, Array.Empty<CatalogAsset>());
            var closure = catalog.GetBundleClosure("root");
            AssertEqual(4, closure.Count, "shared dep appears once");
            AssertEqual("c", closure[0].Name, "shared dep first");
            AssertEqual("root", closure[closure.Count - 1].Name, "root last");
        }

        private static void Closure_Cycle()
        {
            var bundles = new[]
            {
                new CatalogBundle { Name = "x", Hash = "hx", Dependencies = new[] { "y" } },
                new CatalogBundle { Name = "y", Hash = "hy", Dependencies = new[] { "x" } },
            };
            var catalog = new Catalog(bundles, Array.Empty<CatalogAsset>());
            AssertThrows<InvalidOperationException>(() => catalog.GetBundleClosure("x"), "cycle throws");
        }

        private static void Closure_Missing()
        {
            var bundles = new[] { new CatalogBundle { Name = "x", Hash = "hx", Dependencies = new[] { "ghost" } } };
            var catalog = new Catalog(bundles, Array.Empty<CatalogAsset>());
            AssertThrows<InvalidOperationException>(() => catalog.GetBundleClosure("x"), "missing dep throws");
        }

        private static void Ensure_DownloadsAndCaches(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("hello-bundle");
            string hash = ContentHash.Compute(payload);
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport();
            transport.Put(Url(hash), payload);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            string path = Wait(provisioner.EnsureBundleAsync(bundle));
            Assert(File.Exists(path), "cache file written");
            AssertEqual(hash, ContentHash.ComputeFile(path), "cached content matches hash");
            AssertEqual(1, transport.Calls, "downloaded once");
        }

        private static void Ensure_CacheHit(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("cache-hit");
            string hash = ContentHash.Compute(payload);
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport();
            transport.Put(Url(hash), payload);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            Wait(provisioner.EnsureBundleAsync(bundle));
            Wait(provisioner.EnsureBundleAsync(bundle));
            AssertEqual(1, transport.Calls, "second call served from cache");
        }

        private static void Ensure_OfflineCache(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("offline");
            string hash = ContentHash.Compute(payload);
            File.WriteAllBytes(Path.Combine(dir, hash), payload); // pre-populate cache
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport(); // nothing registered → would throw if hit
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            string path = Wait(provisioner.EnsureBundleAsync(bundle));
            Assert(File.Exists(path), "served from pre-populated cache");
            AssertEqual(0, transport.Calls, "no download when cache valid");
        }

        private static void Ensure_RetrySucceeds(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("flaky");
            string hash = ContentHash.Compute(payload);
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport { FailFirst = 2 };
            transport.Put(Url(hash), payload);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 3);

            string path = Wait(provisioner.EnsureBundleAsync(bundle));
            Assert(File.Exists(path), "succeeds on third attempt");
            AssertEqual(3, transport.Calls, "two failures then success");
        }

        private static void Ensure_FailsAfterMax(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("never");
            string hash = ContentHash.Compute(payload);
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport { FailFirst = 99 };
            transport.Put(Url(hash), payload);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 3);

            AssertThrows<ContentDeliveryException>(() => Wait(provisioner.EnsureBundleAsync(bundle)), "gives up after max");
            AssertEqual(3, transport.Calls, "tried exactly maxAttempts times");
        }

        private static void Ensure_HashMismatch(string root)
        {
            var dir = Fresh(root);
            var bundle = new CatalogBundle { Name = "b", Hash = "expected-hash-that-wont-match" };
            var transport = new FakeTransport();
            transport.Put(Url(bundle.Hash), Payload("wrong-bytes"));
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 2);

            AssertThrows<ContentDeliveryException>(() => Wait(provisioner.EnsureBundleAsync(bundle)), "mismatch rejected");
            Assert(!File.Exists(Path.Combine(dir, bundle.Hash)), "corrupt bytes not cached");
        }

        private static void Ensure_TrustsCacheByName(string root)
        {
            var dir = Fresh(root);
            byte[] real = Payload("real");
            string hash = ContentHash.Compute(real);
            // A file present under its content-addressed name is trusted as-is: the per-launch re-hash of large
            // bundles was deliberately dropped (atomic writes already guarantee a present file is complete), so
            // the provisioner serves it without touching the network or re-hashing.
            File.WriteAllBytes(Path.Combine(dir, hash), Payload("stale-but-present"));
            var bundle = new CatalogBundle { Name = "b", Hash = hash };
            var transport = new FakeTransport();
            transport.Put(Url(hash), real); // available — but must NOT be used
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            string path = Wait(provisioner.EnsureBundleAsync(bundle));
            Assert(File.Exists(path), "present cache file is served");
            AssertEqual(0, transport.Calls, "trusted by content-addressed name → no re-download, no re-hash");
        }

        private static void Ensure_Closure(string root)
        {
            var dir = Fresh(root);
            byte[] sharedBytes = Payload("shared");
            byte[] uiBytes = Payload("ui");
            string sharedHash = ContentHash.Compute(sharedBytes);
            string uiHash = ContentHash.Compute(uiBytes);
            var bundles = new[]
            {
                new CatalogBundle { Name = "bundle-shared", Hash = sharedHash },
                new CatalogBundle { Name = "bundle-ui", Hash = uiHash, Dependencies = new[] { "bundle-shared" } },
            };
            var catalog = new Catalog(bundles, Array.Empty<CatalogAsset>());
            var transport = new FakeTransport();
            transport.Put(Url(sharedHash), sharedBytes);
            transport.Put(Url(uiHash), uiBytes);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            var paths = Wait(provisioner.EnsureClosureAsync(catalog.GetBundleClosure("bundle-ui")));
            AssertEqual(2, paths.Count, "both bundles provisioned");
            AssertEqual(2, transport.Calls, "each bundle downloaded once");
            foreach (var p in paths) Assert(File.Exists(p), "closure file exists");
        }

        // ---- fixtures ----------------------------------------------------------------------------

        private static Catalog MakeCatalog()
        {
            var bundles = new[]
            {
                new CatalogBundle { Name = "bundle-shared", Hash = "h-shared" },
                new CatalogBundle { Name = "bundle-ui", Hash = "h-ui", Dependencies = new[] { "bundle-shared" } },
            };
            var assets = new[]
            {
                new CatalogAsset { Address = "ui/hud", Bundle = "bundle-ui", AssetName = "HUD", Phase = 0 },
            };
            return new Catalog(bundles, assets);
        }

        private static string Url(string hash) => "https://cdn/" + hash;
        private static byte[] Payload(string s) => System.Text.Encoding.UTF8.GetBytes(s);

        private static string Fresh(string root)
        {
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// In-memory transport: a url→bytes map, a call counter, optional N-failures-first, optional N-hangs-first
        /// (each hangs until the token cancels — the stall watchdog), and a global corrupt-bytes toggle.
        /// </summary>
        private sealed class FakeTransport : IDownloadTransport
        {
            private readonly Dictionary<string, byte[]> _content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public int Calls;
            public int FailFirst;
            public int HangFirst;
            public bool Corrupt; // always return bytes that will NOT match the expected hash

            public void Put(string url, byte[] bytes) => _content[url] = bytes;

            public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
            {
                int call = Interlocked.Increment(ref Calls);
                if (call <= HangFirst) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                if (call <= FailFirst) throw new IOException("simulated transient failure");
                if (Corrupt) return Payload("corrupt-bytes-that-will-not-match-" + call);
                if (!_content.TryGetValue(url, out var bytes)) throw new IOException("404 " + url);
                return bytes;
            }

            public Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
            {
                var bytes = DownloadBytesAsync(url, cancellationToken).GetAwaiter().GetResult();
                File.WriteAllBytes(destinationPath, bytes);
                return Task.CompletedTask;
            }
        }

        // ---- harness -----------------------------------------------------------------------------

        private static T Wait<T>(Task<T> task)
        {
            try { return task.GetAwaiter().GetResult(); }
            catch (AggregateException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                s_passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception e)
            {
                s_failed++;
                Console.WriteLine("  FAIL  " + name + "  -> " + e.Message);
            }
        }

        private static void Ensure_DecompressesLzmaBundle(string root)
        {
            var dir = Fresh(root);
            byte[] raw = Payload("the original uncompressed bundle bytes — repeated repeated repeated repeated");
            byte[] stored = Lzma.Compress(raw);   // the transferred object is the LZMA blob
            string hash = ContentHash.Compute(stored);        // names the remote object
            string rawHash = ContentHash.Compute(raw);        // names the 1× decompressed cache file
            var bundle = new CatalogBundle { Name = "b", Hash = hash, UncompressedHash = rawHash, Compression = BundleCompression.Lzma };
            var transport = new FakeTransport();
            transport.Put(Url(hash), stored);
            var provisioner = new BundleProvisioner(transport, dir, "https://cdn/");

            string path = Wait(provisioner.EnsureBundleAsync(bundle));
            AssertEqual(Path.Combine(dir, rawHash), path, "loadable cache file named by the uncompressed hash");
            byte[] loaded = File.ReadAllBytes(path);
            Assert(loaded.Length == raw.Length, "decompressed length matches the raw bundle");
            for (int i = 0; i < raw.Length; i++) Assert(loaded[i] == raw[i], "decompressed bytes match the raw bundle");
            Assert(!File.Exists(Path.Combine(dir, hash)), "compressed blob is NOT persisted (cache stays 1×)");

            Wait(provisioner.EnsureBundleAsync(bundle));
            AssertEqual(1, transport.Calls, "decompressed bundle served from cache on the second call (no re-download)");
        }

        private static void Lzma_RoundTripEdge()
        {
            foreach (var data in new[] { new byte[0], new byte[] { 7 }, new byte[] { 1, 2, 3 }, new byte[2000] })
                Assert(LzmaRoundTrips(data), "round-trip identity for edge input len=" + data.Length);
        }

        private static void Lzma_RoundTripCompressible()
        {
            var data = new byte[8000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)("ABCABC"[i % 6]);
            byte[] comp = Lzma.Compress(data);
            Assert(LzmaRoundTrips(data), "round-trip identity for repetitive data");
            Assert(comp.Length < data.Length / 4, "repetitive data should compress well (got " + comp.Length + ")");
        }

        private static void Lzma_RoundTripRandom()
        {
            var rng = new Random(987);
            var data = new byte[16000];
            rng.NextBytes(data);
            Assert(LzmaRoundTrips(data), "round-trip identity for random data");
        }

        private static void CatalogBinary_RoundTrips()
        {
            var bundles = new[]
            {
                new CatalogBundle { Name = "shared", Hash = "hs", Dependencies = Array.Empty<string>(), Local = true, Compression = BundleCompression.None },
                new CatalogBundle { Name = "ui", Hash = "hu", Dependencies = new[] { "shared" }, Local = false, Compression = BundleCompression.Lzma },
            };
            var assets = new[]
            {
                new CatalogAsset { Address = "ui/menu", Bundle = "ui", AssetName = "MainMenu", Phase = 100, Labels = new[] { "ui", "boot" } },
                new CatalogAsset { Address = "ui/bare", Bundle = "ui", AssetName = "Bare", Phase = 0 }, // default (empty) labels
            };
            var packs = new[] { new CatalogPack { Name = "core", Bundles = new[] { "ui", "shared" } } };
            var back = CatalogBinary.Read(CatalogBinary.Write(new Catalog(bundles, assets, "v-bin-1", packs)));

            AssertEqual("v-bin-1", back.ContentHash, "content hash preserved");
            Assert(back.TryGetPack("core", out var core), "pack survives the round-trip");
            AssertEqual(2, core.Bundles.Length, "pack member count preserved");
            AssertEqual("ui", core.Bundles[0], "pack member order preserved");
            AssertEqual("shared", core.Bundles[1], "second pack member preserved");
            Assert(back.TryResolveAsset("ui/menu", out var a), "asset resolves");
            AssertEqual("ui", a.Bundle, "asset bundle preserved");
            AssertEqual("MainMenu", a.AssetName, "alias asset name preserved");
            AssertEqual(100, a.Phase, "phase preserved");
            AssertEqual(2, a.Labels.Length, "labels preserved");
            AssertEqual("ui", a.Labels[0], "first label preserved");
            AssertEqual("boot", a.Labels[1], "second label preserved");
            Assert(back.TryResolveAsset("ui/bare", out var bare) && bare.Labels.Length == 0, "absent labels read back as empty, not null");
            Assert(back.TryGetBundle("ui", out var ui), "bundle present");
            AssertEqual("hu", ui.Hash, "hash preserved");
            AssertEqual(BundleCompression.Lzma, ui.Compression, "compression flag preserved");
            Assert(!ui.Local, "remote flag preserved");
            Assert(back.TryGetBundle("shared", out var sh) && sh.Local, "local flag preserved");
            var closure = back.GetBundleClosure("ui");
            AssertEqual("shared", closure[0].Name, "dependencies survived (deps-first)");
            AssertEqual("ui", closure[1].Name, "closure order preserved");
        }

        private static void CatalogBinary_RejectsBadMagic()
        {
            AssertThrows<ContentDeliveryException>(
                () => CatalogBinary.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "non-catalog bytes rejected");
        }

        private sealed class CapturingProgress : System.IProgress<SchedulerProgress>
        {
            private readonly object _lock = new object();
            public SchedulerProgress Last;
            public int MaxCompleted;
            public void Report(SchedulerProgress p)
            {
                lock (_lock) { Last = p; if (p.Completed > MaxCompleted) MaxCompleted = p.Completed; }
            }
        }

        private static List<CatalogBundle> PublishBundles(FakeTransport transport, int n)
        {
            var bundles = new List<CatalogBundle>(n);
            for (int i = 0; i < n; i++)
            {
                byte[] payload = Payload("scheduler-bundle-" + i + "-" + new string((char)('a' + i), 50));
                string hash = ContentHash.Compute(payload);
                transport.Put(Url(hash), payload);
                bundles.Add(new CatalogBundle { Name = "b" + i, Hash = hash });
            }
            return bundles;
        }

        private static void Scheduler_Balanced(string root)
        {
            var dir = Fresh(root);
            var transport = new FakeTransport();
            var bundles = PublishBundles(transport, 7);
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/"),
                new DownloadSchedulerOptions { MaxConcurrency = 3, RetryBackoff = TimeSpan.Zero });

            var progress = new CapturingProgress();
            var result = Wait(scheduler.ProvisionAsync(bundles, progress));

            AssertEqual(7, result.Succeeded, "all bundles provisioned");
            AssertEqual(0, result.Failed, "none failed");
            Assert(result.BytesProvisioned > 0, "bytes provisioned tracked");
            AssertEqual(7, progress.MaxCompleted, "progress reached the total");
            foreach (var p in result.Paths) Assert(p != null && File.Exists(p), "each path cached");
        }

        private static void Scheduler_Sequential(string root)
        {
            var dir = Fresh(root);
            var transport = new FakeTransport();
            var bundles = PublishBundles(transport, 4);
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/"),
                new DownloadSchedulerOptions { Concurrency = SchedulerConcurrency.Sequential, RetryBackoff = TimeSpan.Zero });

            var result = Wait(scheduler.ProvisionAsync(bundles));
            AssertEqual(4, result.Succeeded, "all bundles provisioned sequentially");
            AssertEqual(4, transport.Calls, "one download per bundle");
        }

        private static void Scheduler_RetriesTransient(string root)
        {
            var dir = Fresh(root);
            var transport = new FakeTransport { FailFirst = 2 }; // first two attempts throw, third succeeds
            var bundles = PublishBundles(transport, 1);
            // provisioner gives up after 1 internal attempt → the scheduler's budget drives the retries.
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 1),
                new DownloadSchedulerOptions { MaxItemRetries = 5, RetryBackoff = TimeSpan.Zero });

            var result = Wait(scheduler.ProvisionAsync(bundles));
            AssertEqual(1, result.Succeeded, "succeeded after retrying the transient failures");
            AssertEqual(3, transport.Calls, "two failures then a success");
        }

        private static void Scheduler_DiskPrecheck(string root)
        {
            var dir = Fresh(root);
            var transport = new FakeTransport();
            var bundles = PublishBundles(transport, 1);
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/"),
                new DownloadSchedulerOptions { CacheDirectory = dir, EstimatedTotalBytes = long.MaxValue / 2 });

            AssertThrows<NotEnoughDiskCapacityException>(
                () => Wait(scheduler.ProvisionAsync(bundles)), "an impossible disk estimate is rejected up front");
        }

        private static void Scheduler_IntegrityRetries(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("integrity");
            string hash = ContentHash.Compute(payload);
            var bundles = new[] { new CatalogBundle { Name = "b", Hash = hash } };
            var transport = new FakeTransport { Corrupt = true }; // every fetch returns bytes that fail the hash
            transport.Put(Url(hash), payload);
            // provisioner gives up after 1 internal attempt so the scheduler's integrity budget drives the retries.
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 1),
                new DownloadSchedulerOptions
                {
                    MaxItemRetries = 10,          // a big TRANSIENT budget must NOT be used for corrupt bytes
                    MaxIntegrityRetries = 3,
                    IntegrityRetryBackoff = TimeSpan.Zero,
                    RetryBackoff = TimeSpan.Zero,
                });

            var result = Wait(scheduler.ProvisionAsync(bundles));
            AssertEqual(0, result.Succeeded, "corrupt bundle never provisions");
            AssertEqual(1, result.Failed, "reported as failed");
            AssertEqual(3, transport.Calls, "tried exactly the integrity budget (not the larger transient budget)");
        }

        private static void Scheduler_StallWatchdog(string root)
        {
            var dir = Fresh(root);
            byte[] payload = Payload("stall-then-ok");
            string hash = ContentHash.Compute(payload);
            var bundles = new[] { new CatalogBundle { Name = "b", Hash = hash } };
            var transport = new FakeTransport { HangFirst = 1 }; // first attempt hangs; second returns good bytes
            transport.Put(Url(hash), payload);
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/", maxAttempts: 1),
                new DownloadSchedulerOptions
                {
                    StallTimeout = TimeSpan.FromMilliseconds(150),
                    MaxItemRetries = 3,
                    RetryBackoff = TimeSpan.Zero,
                });

            var result = Wait(scheduler.ProvisionAsync(bundles));
            AssertEqual(1, result.Succeeded, "recovered after the stall watchdog abandoned the hung attempt");
            AssertEqual(2, transport.Calls, "one hung attempt (timed out) then one good attempt");
        }

        private static void Scheduler_FeedsSpeedMeter(string root)
        {
            var dir = Fresh(root);
            var transport = new FakeTransport();
            var bundles = PublishBundles(transport, 3);
            // A threshold above any achievable in-memory rate is impossible to beat → the meter must flip to Bad.
            var meter = new DownloadSpeedMeter(double.MaxValue);
            bool wentBad = false;
            meter.BadDetected += () => wentBad = true;
            var scheduler = new DownloadScheduler(new BundleProvisioner(transport, dir, "https://cdn/"),
                new DownloadSchedulerOptions { RetryBackoff = TimeSpan.Zero, SpeedMeter = meter });

            var result = Wait(scheduler.ProvisionAsync(bundles));
            AssertEqual(3, result.Succeeded, "all provisioned");
            Assert(wentBad, "the speed meter was fed and classified the (sub-threshold) rate as Bad");
        }

        private static void SpeedMeter_DirectTransitions()
        {
            var meter = new DownloadSpeedMeter(1000); // 1000 B/s threshold
            int changes = 0; DownloadSpeedRating lastRating = DownloadSpeedRating.Good;
            int bad = 0, good = 0;
            meter.RatingChanged += r => { changes++; lastRating = r; };
            meter.BadDetected += () => bad++;
            meter.GoodDetected += () => good++;

            AssertEqual(DownloadSpeedRating.Good, meter.Rating, "starts optimistic (Good)");
            meter.Sample(2000);  // still good — no transition
            AssertEqual(0, changes, "no event while staying Good");
            meter.Sample(500);   // Good → Bad
            AssertEqual(1, changes, "one transition to Bad");
            AssertEqual(1, bad, "BadDetected fired");
            AssertEqual(DownloadSpeedRating.Bad, lastRating, "rating is Bad");
            meter.Sample(400);   // still bad — no transition
            AssertEqual(1, changes, "no event while staying Bad");
            meter.Sample(5000);  // Bad → Good
            AssertEqual(2, changes, "one transition back to Good");
            AssertEqual(1, good, "GoodDetected fired");
        }

        private static void SpeedMeter_IntervalGating()
        {
            var meter = new DownloadSpeedMeter(1000) { CheckInterval = 1.0 };
            int bad = 0;
            meter.BadDetected += () => bad++;

            meter.Sample(0.0, 0);      // seed baseline (no classification)
            meter.Sample(0.5, 100);    // 0.5s elapsed < interval → ignored (a burst can't flip it)
            AssertEqual(0, bad, "sub-interval reading ignored");
            meter.Sample(2.0, 300);    // now 2.0s since baseline, 300 bytes → 150 B/s < 1000 → Bad
            AssertEqual(1, bad, "slow sustained rate classified Bad once the interval elapsed");
        }

        private static void DeferredQueue_ReleasesAfterFrames()
        {
            var q = new DeferredUnloadQueue<int>(frames: 2);
            var released = new List<int>();
            q.Enqueue(7, currentFrame: 100);
            q.Pump(101, released.Add);  // 1 frame later — still in the grace window
            AssertEqual(0, released.Count, "not released before the window elapses");
            Assert(q.IsQueued(7), "still queued");
            q.Pump(102, released.Add);  // 2 frames later — due
            AssertEqual(1, released.Count, "released once the window elapses");
            AssertEqual(7, released[0], "released the right key");
            Assert(!q.IsQueued(7), "dropped from the queue after release");
        }

        private static void DeferredQueue_CancelRevives()
        {
            var q = new DeferredUnloadQueue<int>(frames: 2);
            var released = new List<int>();
            q.Enqueue(5, 100);
            Assert(q.Cancel(5), "cancel reports a pending release existed");
            q.Pump(200, released.Add); // long past the window
            AssertEqual(0, released.Count, "a cancelled (re-acquired) key is never released");
            AssertEqual(0, q.Count, "queue empty after cancel");
        }

        private static void DeferredQueue_Flush()
        {
            var q = new DeferredUnloadQueue<int>(frames: 100);
            var released = new List<int>();
            q.Enqueue(1, 0); q.Enqueue(2, 0); q.Enqueue(3, 0);
            q.Flush(released.Add); // even well within the window, flush releases everything now
            released.Sort();
            AssertEqual(3, released.Count, "flush released all pending keys");
            AssertEqual(0, q.Count, "queue empty after flush");
        }

        private static void Lzma_PooledConcurrent()
        {
            // Distinct payloads compressed once, then decompressed many times concurrently through the decoder +
            // buffer pools, alternating the array and stream entry points. A pooling/reset bug shows as corruption.
            var rng = new Random(555);
            const int n = 24;
            var raws = new byte[n][];
            var comps = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                raws[i] = new byte[2000 + i * 137];
                rng.NextBytes(raws[i]);
                for (int j = 0; j < 300 && j < raws[i].Length; j++) raws[i][j] = (byte)(i & 0xFF); // compressible head
                comps[i] = Lzma.Compress(raws[i]);
            }

            int failures = 0;
            var tasks = new List<Task>();
            for (int t = 0; t < 64; t++)
            {
                int idx = t % n;
                bool stream = (t & 1) == 0;
                tasks.Add(Task.Run(() =>
                {
                    byte[] got;
                    if (stream)
                    {
                        using (var ms = new MemoryStream())
                        {
                            Lzma.DecompressInto(comps[idx], ms);
                            got = ms.ToArray();
                        }
                    }
                    else got = Lzma.Decompress(comps[idx]);

                    if (got.Length != raws[idx].Length) { Interlocked.Increment(ref failures); return; }
                    for (int k = 0; k < got.Length; k++)
                        if (got[k] != raws[idx][k]) { Interlocked.Increment(ref failures); return; }
                }));
            }
            Task.WaitAll(tasks.ToArray());
            Assert(failures == 0, "all concurrent pooled decompressions correct (failures=" + failures + ")");
        }

        private static void Lzma_WrapsWindow()
        {
            // Output larger than the 4 MB DictSize forces the decoder's circular window to wrap + flush mid-stream;
            // a wrap bug shows as corruption past the boundary. Repetitive content compresses fast and yields long
            // matches that span the boundary.
            var data = new byte[5 * 1024 * 1024];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)("PFoundContentDelivery"[i % 21]);
            Assert(LzmaRoundTrips(data), "round-trip identity across the window wrap (output > DictSize)");
        }

        private static bool LzmaRoundTrips(byte[] data)
        {
            byte[] back = Lzma.Decompress(Lzma.Compress(data));
            if (back.Length != data.Length) return false;
            for (int i = 0; i < data.Length; i++) if (back[i] != data[i]) return false;
            return true;
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new Exception("assertion failed: " + what);
        }

        private static void AssertEqual(object expected, object actual, string what)
        {
            if (!Equals(expected, actual))
                throw new Exception("expected <" + expected + "> but got <" + actual + "> for " + what);
        }

        private static void AssertThrows<TException>(Action action, string what) where TException : Exception
        {
            try { action(); }
            catch (TException) { return; }
            catch (Exception e) { throw new Exception("expected " + typeof(TException).Name + " but got " + e.GetType().Name + " for " + what); }
            throw new Exception("expected " + typeof(TException).Name + " but nothing was thrown for " + what);
        }
    }
}
