using System;
using PFound.Compression;

namespace PFound.ContentDelivery.Core
{
    /// <summary>
    /// Decodes catalog bytes in whatever PFound catalog form the embedded/remote reader hands it, auto-detecting
    /// the shape from the leading bytes so a single call handles all of them:
    /// <list type="bullet">
    ///   <item>PFound binary (<c>CatalogBinary</c>, magic <c>PFCB</c>) — compact, fast to parse.</item>
    ///   <item>PFound JSON (<c>CatalogJson</c>) — human-readable; parsed by the injected <paramref name="jsonParser"/>
    ///         because JSON parsing lives in the Unity layer (<c>JsonUtility</c>), keeping this Core class engine-free.</item>
    ///   <item>Either of the above wrapped in PFound's LZMA — decompressed in-flight, then re-detected.</item>
    /// </list>
    /// This is the CATALOG-FORMAT decision made concrete: the app rebuilds catalogs through PFound's own pipeline
    /// (binary or JSON, optionally LZMA-compressed for the embedded package); there is deliberately no legacy binary
    /// path. LZMA reuses <see cref="Lzma"/>, the same codec the bundle pipeline already uses.
    /// </summary>
    public static class CatalogCodec
    {
        private const int MaxDecompressDepth = 2; // one LZMA layer is expected; a second guards a mis-detection loop.

        /// <summary>
        /// Decodes <paramref name="bytes"/> to a <see cref="Catalog"/>. <paramref name="jsonParser"/> turns a JSON
        /// byte payload into a catalog (the Unity layer passes its <c>CatalogJson</c>-backed parser). Throws
        /// <see cref="ContentDeliveryException"/> when the bytes match no known form.
        /// </summary>
        public static Catalog Decode(byte[] bytes, Func<byte[], Catalog> jsonParser)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (jsonParser == null) throw new ArgumentNullException(nameof(jsonParser));
            return Decode(bytes, jsonParser, 0);
        }

        private static Catalog Decode(byte[] bytes, Func<byte[], Catalog> jsonParser, int depth)
        {
            if (LooksLikeBinaryCatalog(bytes)) return CatalogBinary.Read(bytes);
            if (LooksLikeJson(bytes)) return jsonParser(bytes);

            if (depth < MaxDecompressDepth && LooksLikeLzma(bytes))
                return Decode(Lzma.Decompress(bytes), jsonParser, depth + 1);

            throw new ContentDeliveryException("Unrecognized catalog format (not PFound binary, JSON, or LZMA).");
        }

        // 'P''F''C''B' — the CatalogBinary magic, little-endian 0x42434650.
        private static bool LooksLikeBinaryCatalog(byte[] b) =>
            b.Length >= 4 && b[0] == (byte)'P' && b[1] == (byte)'F' && b[2] == (byte)'C' && b[3] == (byte)'B';

        // First non-whitespace byte is '{' — a JSON object document.
        private static bool LooksLikeJson(byte[] b)
        {
            int i = 0;
            // Tolerate a UTF-8 BOM.
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) i = 3;
            for (; i < b.Length; i++)
            {
                byte c = b[i];
                if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n') continue;
                return c == (byte)'{';
            }
            return false;
        }

        // PFound's LZMA "alone" header: a props byte then a 4-byte dict size then an 8-byte length — 13 bytes min.
        // Neither PFCB (starts 'P') nor JSON (starts '{') collide with a valid props byte, so a fall-through here is LZMA.
        private static bool LooksLikeLzma(byte[] b) => b.Length >= 13;
    }
}
