using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using PFound.ContentDelivery.Core;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>One built bundle's summary line for the build report.</summary>
    public struct BundleReportEntry
    {
        public string Name;
        public string Hash;          // stored (transferred/cached) content hash
        public long StoredSize;      // bytes of the stored object (after our-LZMA, if any)
        public bool Local;           // shipped in StreamingAssets vs uploaded to the CDN
        public string Compression;   // transfer compression of the stored object
    }

    /// <summary>Result of a content build: where the publish-ready files live and the catalog that maps them.</summary>
    public struct ContentBuildReport
    {
        public string PublishDirectory;   // folder of content-addressed REMOTE bundle files + catalog.json, ready to upload
        public string CatalogPath;        // catalog.json inside PublishDirectory
        public string CatalogJson;        // the catalog document (also written to CatalogPath + StreamingAssets)
        public int BundleCount;
        public int LocalBundleCount;      // bundles routed to StreamingAssets (ship in the build)
        public int RemoteBundleCount;     // bundles routed to publish/ (uploaded to the CDN)
        public string StreamingAssetsDirectory; // where Local bundles + the bootstrap catalog were written
        public string CatalogVersion;     // content-derived version stamped into the catalog
        public BundleReportEntry[] Bundles; // per-bundle summary (name, hash, stored size, routing)
    }

    /// <summary>
    /// Orchestrates a content build on the Scriptable Build Pipeline (<see cref="ContentPipeline"/>): turns
    /// <see cref="AssetGroup"/>s into AssetBundles, content-addresses each output by its hash (so the remote object
    /// name and the cache file name are the hash), and emits the runtime catalog JSON. Bundle dependency edges come
    /// from SBP's per-bundle results; the runtime walks the closure. SBP gives deterministic hashes, an incremental
    /// build cache, and per-object WriteResults (the basis for a later duplicate/waste analyzer). Editor-only.
    /// </summary>
    public static class BundleBuildPipeline
    {
        /// <summary>Builds for the active editor build target into <paramref name="outputDirectory"/>.</summary>
        public static ContentBuildReport Build(IReadOnlyList<AssetGroup> groups, string outputDirectory) =>
            Build(groups, outputDirectory, EditorUserBuildSettings.activeBuildTarget);

        /// <param name="hasher">
        /// Content hasher for bundle naming; null = xxHash3 (the runtime default). The runtime verifies with the
        /// same algorithm, so this MUST match what the consuming app constructs its source with.
        /// </param>
        /// <param name="compression">
        /// Transfer compression for the stored objects. <c>Lzma</c> (default) builds bundles uncompressed (so our
        /// LZMA is effective) then LZMA-compresses each — smaller downloads than Unity's LZ4; the runtime
        /// decompresses. <c>None</c> keeps Unity's LZ4 chunk compression and stores the bundle as-is.
        /// </param>
        public static ContentBuildReport Build(
            IReadOnlyList<AssetGroup> groups, string outputDirectory, BuildTarget target,
            Core.IContentHasher hasher = null, Core.BundleCompression compression = Core.BundleCompression.Lzma)
        {
            if (groups == null) throw new System.ArgumentNullException(nameof(groups));
            if (string.IsNullOrEmpty(outputDirectory)) throw new System.ArgumentException("Output directory required.", nameof(outputDirectory));
            hasher = hasher ?? new XxHash3ContentHasher();

            var builds = new List<AssetBundleBuild>();
            var addresses = new List<CatalogBuilder.AddressRecord>();
            var bundleLocal = new Dictionary<string, bool>();
            var packMembers = new Dictionary<string, List<string>>(); // pack name → produced bundle names
            CollectBuilds(groups, builds, addresses, bundleLocal, packMembers);

            string staging = Path.Combine(outputDirectory, "staging");
            Directory.CreateDirectory(staging);

            // For our-LZMA we want raw (Uncompressed) bundles so the LZMA pass has uncompressed bytes to work on;
            // for None we keep SBP's LZ4 archive and store the bundle as-is.
            var buildParams = new BundleBuildParameters(target, BuildPipeline.GetBuildTargetGroup(target), staging)
            {
                BundleCompression = compression == Core.BundleCompression.Lzma
                    ? BuildCompression.Uncompressed
                    : BuildCompression.LZ4,
            };
            var buildContent = new BundleBuildContent(builds);
            ReturnCode code = ContentPipeline.BuildAssetBundles(buildParams, buildContent, out IBundleBuildResults results);
            if (code < ReturnCode.Success)
                throw new ContentDeliveryException("SBP build failed (" + code + ").");

            // Stored object per bundle = the bytes we content-address, transfer and cache: the raw bundle, or its
            // LZMA. Hash the STORED bytes (that is what the runtime downloads + verifies).
            var built = new List<CatalogBuilder.BuiltBundle>(builds.Count);
            var storedBytes = new Dictionary<string, byte[]>(builds.Count);
            for (int i = 0; i < builds.Count; i++)
            {
                string name = builds[i].assetBundleName;
                byte[] raw = File.ReadAllBytes(Path.Combine(staging, name));
                byte[] stored = compression == Core.BundleCompression.Lzma ? PFound.Lzma.Lzma.Compress(raw) : raw;
                storedBytes[name] = stored;
                built.Add(new CatalogBuilder.BuiltBundle
                {
                    Name = name,
                    Hash = hasher.Compute(stored),                 // the transferred/cached-blob hash
                    UncompressedHash = hasher.Compute(raw),        // names the 1× decompressed cache file at runtime
                    DirectDependencies = results.BundleInfos[name].Dependencies,
                    Local = bundleLocal.TryGetValue(name, out bool local) && local,
                    Compression = compression,
                });
            }

            // Content-derived catalog version: a digest of all bundle hashes, so the version changes iff any
            // bundle content changes (deterministic, no timestamp). The runtime compares it to decide a re-pull.
            var versionInput = new System.Text.StringBuilder();
            for (int i = 0; i < built.Count; i++) versionInput.Append(built[i].Hash).Append(';');
            string version = hasher.Compute(System.Text.Encoding.UTF8.GetBytes(versionInput.ToString()));

            var packs = new List<CatalogBuilder.PackRecord>(packMembers.Count);
            foreach (var kv in packMembers)
                packs.Add(new CatalogBuilder.PackRecord { Name = kv.Key, Bundles = kv.Value.ToArray() });

            string catalogJson = CatalogBuilder.ToJson(addresses, built, packs, version);

            // Publish (remote origin / CDN): only Remote bundles need uploading. Local bundles ship in the
            // build, so they go to StreamingAssets instead. The catalog is written to BOTH: StreamingAssets so
            // the app boots offline from shipped content, and publish/ so a newer catalog can override remotely.
            string publish = Path.Combine(outputDirectory, "publish");
            if (Directory.Exists(publish)) Directory.Delete(publish, true);
            Directory.CreateDirectory(publish);

            string streamingContent = ContentDeliveryPaths.StreamingAssetsContentDirectory;
            Directory.CreateDirectory(streamingContent);

            int localCount = 0, remoteCount = 0;
            var entries = new BundleReportEntry[built.Count];
            for (int i = 0; i < built.Count; i++)
            {
                byte[] stored = storedBytes[built[i].Name];
                string dest = built[i].Local ? streamingContent : publish;
                File.WriteAllBytes(Path.Combine(dest, built[i].Hash), stored);
                if (built[i].Local) localCount++; else remoteCount++;
                entries[i] = new BundleReportEntry
                {
                    Name = built[i].Name,
                    Hash = built[i].Hash,
                    StoredSize = stored.Length,
                    Local = built[i].Local,
                    Compression = built[i].Compression.ToString(),
                };
            }

            string catalogPath = Path.Combine(publish, ContentDeliveryPaths.CatalogFileName);
            File.WriteAllText(catalogPath, catalogJson);
            File.WriteAllText(Path.Combine(streamingContent, ContentDeliveryPaths.CatalogFileName), catalogJson);
            AssetDatabase.Refresh();

            return new ContentBuildReport
            {
                PublishDirectory = publish,
                CatalogPath = catalogPath,
                CatalogJson = catalogJson,
                BundleCount = built.Count,
                LocalBundleCount = localCount,
                RemoteBundleCount = remoteCount,
                StreamingAssetsDirectory = streamingContent,
                CatalogVersion = version,
                Bundles = entries,
            };
        }

        private static void CollectBuilds(
            IReadOnlyList<AssetGroup> groups, List<AssetBundleBuild> builds,
            List<CatalogBuilder.AddressRecord> addresses, Dictionary<string, bool> bundleLocal,
            Dictionary<string, List<string>> packMembers)
        {
            foreach (var group in groups)
            {
                if (group == null || group.Entries == null) continue;
                string groupBundle = group.ResolveBundleName();
                int phase = (int)group.Phase;
                bool local = group.Distribution == DistributionMode.Local;
                string pack = group.Pack;

                if (group.Packing == BundlePackingMode.PackSeparately)
                {
                    foreach (var e in group.Entries)
                    {
                        if (!TryGetAssetPath(e, out string path)) continue;
                        string bundleName = PerAssetBundleName(groupBundle, e.Address);
                        builds.Add(new AssetBundleBuild
                        {
                            assetBundleName = bundleName,
                            assetNames = new[] { path },
                            addressableNames = new[] { e.Address },
                        });
                        addresses.Add(new CatalogBuilder.AddressRecord { Address = e.Address, Bundle = bundleName, Phase = phase, Labels = e.Labels?.ToArray() });
                        bundleLocal[bundleName] = local;
                        AddToPack(packMembers, pack, bundleName);
                    }
                    continue;
                }

                // PackTogether: one bundle holds the whole group.
                var paths = new List<string>();
                var names = new List<string>();
                foreach (var e in group.Entries)
                {
                    if (!TryGetAssetPath(e, out string path)) continue;
                    paths.Add(path);
                    names.Add(e.Address);
                    addresses.Add(new CatalogBuilder.AddressRecord { Address = e.Address, Bundle = groupBundle, Phase = phase, Labels = e.Labels?.ToArray() });
                }
                if (paths.Count == 0) continue;
                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = groupBundle,
                    assetNames = paths.ToArray(),
                    addressableNames = names.ToArray(),
                });
                bundleLocal[groupBundle] = local;
                AddToPack(packMembers, pack, groupBundle);
            }
        }

        // Registers a produced bundle as a member of its group's pack (no-op when the group has no pack; deduped).
        private static void AddToPack(Dictionary<string, List<string>> packMembers, string pack, string bundleName)
        {
            if (string.IsNullOrEmpty(pack)) return;
            if (!packMembers.TryGetValue(pack, out var members))
            {
                members = new List<string>();
                packMembers[pack] = members;
            }
            if (!members.Contains(bundleName)) members.Add(bundleName);
        }

        private static bool TryGetAssetPath(AssetEntry entry, out string path)
        {
            path = null;
            if (entry == null || entry.Asset == null || string.IsNullOrEmpty(entry.Address)) return false;
            path = AssetDatabase.GetAssetPath(entry.Asset);
            return !string.IsNullOrEmpty(path);
        }

        /// <summary>Bundle id for a separately-packed asset; shared by the build and the catalog so they agree.</summary>
        public static string PerAssetBundleName(string groupBundle, string address) => groupBundle + "/" + address;
    }
}
