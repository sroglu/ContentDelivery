using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Hand-rolled compact binary (de)serialization for a <see cref="Catalog"/> — a length-prefixed
    /// <see cref="BinaryWriter"/>/<see cref="BinaryReader"/> format, deliberately NOT a reflection/JSON converter.
    /// Smaller and faster to parse than the JSON form for large catalogs; the JSON form stays the
    /// human-readable / build-debug format. Pure Core (BCL only), engine-free.
    /// Layout: magic "PFCB" · format version byte · catalog version string · bundle[] · asset[] · pack[].
    /// NOTE: <see cref="Catalog.BuildMode"/> is intentionally NOT carried here — it is a JSON-format concern
    /// (the build pipeline only ever emits JSON catalogs). A binary round-trip leaves BuildMode null; adding it
    /// would need a format-version bump that invalidates existing v3 binary caches, for a field this path never
    /// writes. Bump to v4 only if the pipeline ever starts emitting binary catalogs.
    /// </summary>
    public static class CatalogBinary
    {
        private const uint Magic = 0x42434650; // 'P''F''C''B'
        private const byte FormatVersion = 3;  // v2 added per-asset Labels; v3 adds the pack[] section

        public static byte[] Write(Catalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            var bundles = new List<CatalogBundle>(catalog.AllBundles);
            var assets = new List<CatalogAsset>(catalog.AllAssets);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(catalog.Version ?? string.Empty);

                w.Write(bundles.Count);
                foreach (var b in bundles)
                {
                    w.Write(b.Name ?? string.Empty);
                    w.Write(b.Hash ?? string.Empty);
                    w.Write(b.UncompressedHash ?? string.Empty);
                    w.Write((byte)b.Compression);
                    w.Write(b.Local);
                    var deps = b.Dependencies ?? Array.Empty<string>();
                    w.Write(deps.Length);
                    foreach (var d in deps) w.Write(d ?? string.Empty);
                }

                w.Write(assets.Count);
                foreach (var a in assets)
                {
                    w.Write(a.Address ?? string.Empty);
                    w.Write(a.Bundle ?? string.Empty);
                    w.Write(a.AssetName ?? string.Empty);
                    w.Write(a.Phase);
                    var labels = a.Labels ?? Array.Empty<string>();
                    w.Write(labels.Length);
                    foreach (var l in labels) w.Write(l ?? string.Empty);
                }

                var packs = new List<CatalogPack>(catalog.AllPacks);
                w.Write(packs.Count);
                foreach (var p in packs)
                {
                    w.Write(p.Name ?? string.Empty);
                    var members = p.Bundles ?? Array.Empty<string>();
                    w.Write(members.Length);
                    foreach (var m in members) w.Write(m ?? string.Empty);
                }

                w.Flush();
                return ms.ToArray();
            }
        }

        public static Catalog Read(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            using (var ms = new MemoryStream(data, false))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                if (r.ReadUInt32() != Magic)
                    throw new ContentDeliveryException("Not a PFound binary catalog (bad magic).");
                byte format = r.ReadByte();
                if (format != FormatVersion)
                    throw new ContentDeliveryException("Unsupported binary catalog format version: " + format + ".");

                string version = r.ReadString();
                if (version.Length == 0) version = null;

                int bundleCount = r.ReadInt32();
                var bundles = new CatalogBundle[bundleCount];
                for (int i = 0; i < bundleCount; i++)
                {
                    var b = new CatalogBundle
                    {
                        Name = r.ReadString(),
                        Hash = r.ReadString(),
                        UncompressedHash = r.ReadString(),
                        Compression = (BundleCompression)r.ReadByte(),
                        Local = r.ReadBoolean(),
                    };
                    int depCount = r.ReadInt32();
                    var deps = new string[depCount];
                    for (int d = 0; d < depCount; d++) deps[d] = r.ReadString();
                    b.Dependencies = deps;
                    bundles[i] = b;
                }

                int assetCount = r.ReadInt32();
                var assets = new CatalogAsset[assetCount];
                for (int i = 0; i < assetCount; i++)
                {
                    var asset = new CatalogAsset
                    {
                        Address = r.ReadString(),
                        Bundle = r.ReadString(),
                        AssetName = r.ReadString(),
                        Phase = r.ReadInt32(),
                    };
                    int labelCount = r.ReadInt32();
                    var labels = new string[labelCount];
                    for (int l = 0; l < labelCount; l++) labels[l] = r.ReadString();
                    asset.Labels = labels;
                    assets[i] = asset;
                }

                int packCount = r.ReadInt32();
                var packs = new CatalogPack[packCount];
                for (int i = 0; i < packCount; i++)
                {
                    var pack = new CatalogPack { Name = r.ReadString() };
                    int memberCount = r.ReadInt32();
                    var members = new string[memberCount];
                    for (int m = 0; m < memberCount; m++) members[m] = r.ReadString();
                    pack.Bundles = members;
                    packs[i] = pack;
                }

                return new Catalog(bundles, assets, version, packs);
            }
        }
    }
}
