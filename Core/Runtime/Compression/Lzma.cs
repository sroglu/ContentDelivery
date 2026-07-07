using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace PFound.ContentDelivery.Core.Compression
{
    /// <summary>
    /// Clean-room LZMA codec (the LZMA1 algorithm family: a binary range coder over a literal / match model with
    /// rep-distance history and slot-coded lengths and distances), implemented from the published algorithm — NOT
    /// a port of the 7-Zip SDK. It is self-consistent: <see cref="Decompress"/> inverts <see cref="Compress"/>,
    /// which is the only property content delivery needs (the editor compresses and the runtime decompresses with
    /// this same codec). The encoder emits literals and simple matches (greedy hash-chain match finder); it never
    /// chooses rep matches, so the stream is smaller-than-LZ4 but not bit-identical to 7-Zip — that is fine here.
    ///
    /// Stream layout (the classic .lzma/"alone" header): 1 byte packed props (lc,lp,pb) · 4 bytes little-endian
    /// dictionary size · 8 bytes little-endian uncompressed length · range-coded payload.
    /// </summary>
    public static class Lzma
    {
        // --- model constants (LZMA) ---
        private const int kNumBitModelTotalBits = 11;
        private const ushort kBitModelTotal = 1 << kNumBitModelTotalBits;     // 2048
        private const int kNumMoveBits = 5;
        private const uint kTopValue = 1u << 24;

        private const int kNumPosBitsMax = 4;
        private const int kNumStates = 12;
        private const int kNumLenToPosStates = 4;
        private const int kNumAlignBits = 4;
        private const int kStartPosModelIndex = 4;
        private const int kEndPosModelIndex = 14;
        private const int kNumFullDistances = 1 << (kEndPosModelIndex >> 1); // 128
        private const int kNumPosSlotBits = 6;

        private const int kMatchMinLen = 2;
        private const int kNumLenLow = 1 << 3;   // 8
        private const int kNumLenMid = 1 << 3;   // 8
        private const int kNumLenHigh = 1 << 8;  // 256
        private const int kNumLenSymbols = kNumLenLow + kNumLenMid + kNumLenHigh; // 272
        private const int kMatchMaxLen = kMatchMinLen + kNumLenSymbols - 1;       // 273

        // default literal/position context bits
        private const int Lc = 3, Lp = 0, Pb = 2;
        private const int DictSize = 1 << 22; // 4 MB window

        // Decoders are pooled: each carries the probability model state AND its reusable DictSize output window,
        // reset (not reallocated) between uses — so concurrent multi-bundle decompression allocates neither the
        // model arrays nor a per-bundle output buffer. Concurrency is capped by the caller (BundleProvisioner's
        // decompress gate), which bounds how many windows are resident at once.
        private static readonly ConcurrentBag<Decoder> s_decoderPool = new ConcurrentBag<Decoder>();
        private static Decoder RentDecoder() => s_decoderPool.TryTake(out var d) ? d : new Decoder();
        private static void ReturnDecoder(Decoder d) => s_decoderPool.Add(d);

        public static byte[] Compress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return new Encoder().Run(data);
        }

        /// <summary>Decompresses to a freshly-allocated array (the caller owns it). Uses a pooled decoder.</summary>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var outp = new byte[ReadUncompressedLength(data)];
            using (var src = new MemoryStream(data, false))
            using (var dst = new MemoryStream(outp, 0, outp.Length, true, false)) // fixed-size: the window streams in
                DecompressInto(src, dst);
            return outp;
        }

        /// <summary>Convenience: decompress in-memory bytes into a destination stream.</summary>
        public static void DecompressInto(byte[] data, Stream destination)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var src = new MemoryStream(data, false)) DecompressInto(src, destination);
        }

        /// <summary>
        /// Streams both ends: the compressed input is pulled from <paramref name="source"/> sequentially (no full
        /// managed copy of it), decoded through a pooled decoder + a pooled working buffer, and written straight to
        /// <paramref name="destination"/>. The runtime hot path — decompress a downloaded bundle to its cache file —
        /// kept low-peak-memory and allocation-light under concurrency.
        /// </summary>
        public static void DecompressInto(Stream source, Stream destination)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            var dec = RentDecoder();
            try { dec.Decode(source, destination); } finally { ReturnDecoder(dec); }
        }

        /// <summary>The uncompressed length the header at offset 5 declares (after 1 props + 4 dict-size bytes).</summary>
        public static long ReadUncompressedLength(byte[] data)
        {
            ulong len = 0;
            for (int i = 0; i < 8; i++) len |= (ulong)data[5 + i] << (8 * i);
            return checked((long)len);
        }

        // probability helpers shared by encoder/decoder
        private static ushort[] NewProbs(int n)
        {
            var p = new ushort[n];
            for (int i = 0; i < n; i++) p[i] = kBitModelTotal >> 1;
            return p;
        }

        private static int PosSlot(uint dist)
        {
            if (dist < 4) return (int)dist;
            int n = 31; while (((dist >> n) & 1) == 0) n--;        // index of high bit
            return (n << 1) | (int)((dist >> (n - 1)) & 1);
        }

        private static int LenToPosState(int len)
        {
            int s = len - kMatchMinLen;
            return s < kNumLenToPosStates ? s : kNumLenToPosStates - 1;
        }

        // ===================== ENCODER =====================
        private sealed class Encoder
        {
            private readonly List<byte> _out = new List<byte>();
            private ulong _low;
            private uint _range = 0xFFFFFFFF;
            private byte _cache;
            private long _cacheSize = 1;

            // models
            private readonly ushort[] _isMatch = NewProbs(kNumStates << kNumPosBitsMax);
            private readonly ushort[] _isRep = NewProbs(kNumStates);
            private readonly ushort[][] _posSlot = Jagged(kNumLenToPosStates, 1 << kNumPosSlotBits);
            private readonly ushort[] _specPos = NewProbs(kNumFullDistances - kEndPosModelIndex);
            private readonly ushort[] _align = NewProbs(1 << kNumAlignBits);
            private readonly ushort[] _literal = NewProbs((1 << (Lc + Lp)) * 0x300);
            private readonly LenEnc _len = new LenEnc();

            private static ushort[][] Jagged(int a, int b)
            {
                var r = new ushort[a][];
                for (int i = 0; i < a; i++) r[i] = NewProbs(b);
                return r;
            }

            public byte[] Run(byte[] data)
            {
                WriteHeader(data.Length);

                int state = 0;
                uint rep0 = 0; // last match distance, for the matched-literal context
                int posMask = (1 << Pb) - 1;
                var mf = new MatchFinder(data);

                int pos = 0;
                while (pos < data.Length)
                {
                    int posState = pos & posMask;
                    mf.FindLongest(pos, out int len, out uint dist);

                    if (len >= kMatchMinLen)
                    {
                        EncodeBit(_isMatch, (state << kNumPosBitsMax) + posState, 1);
                        EncodeBit(_isRep, state, 0); // always a simple match
                        _len.Encode(this, len - kMatchMinLen, posState);
                        EncodeDistance(len, dist - 1);
                        rep0 = dist - 1;
                        state = state < 7 ? 7 : 10;
                        mf.Insert(pos, len);
                        pos += len;
                    }
                    else
                    {
                        EncodeBit(_isMatch, (state << kNumPosBitsMax) + posState, 0);
                        byte cur = data[pos];
                        byte prev = pos > 0 ? data[pos - 1] : (byte)0;
                        EncodeLiteral(state, prev, cur, data, pos, rep0);
                        state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                        mf.Insert(pos, 1);
                        pos++;
                    }
                }

                Flush();
                return _out.ToArray();
            }

            private void WriteHeader(long uncompressedLength)
            {
                _out.Add((byte)((Pb * 5 + Lp) * 9 + Lc));
                uint d = DictSize;
                for (int i = 0; i < 4; i++) _out.Add((byte)(d >> (8 * i)));
                ulong u = (ulong)uncompressedLength;
                for (int i = 0; i < 8; i++) _out.Add((byte)(u >> (8 * i)));
            }

            // --- range encoder primitives ---
            private void ShiftLow()
            {
                if ((uint)(_low >> 32) != 0 || _low < 0xFF000000UL)
                {
                    byte temp = _cache;
                    do { _out.Add((byte)(temp + (byte)(_low >> 32))); temp = 0xFF; } while (--_cacheSize != 0);
                    _cache = (byte)(_low >> 24);
                }
                _cacheSize++;
                _low = (_low & 0x00FFFFFF) << 8;
            }

            private void EncodeBit(ushort[] probs, int index, int symbol)
            {
                uint prob = probs[index];
                uint bound = (_range >> kNumBitModelTotalBits) * prob;
                if (symbol == 0)
                {
                    _range = bound;
                    probs[index] = (ushort)(prob + ((kBitModelTotal - prob) >> kNumMoveBits));
                }
                else
                {
                    _low += bound;
                    _range -= bound;
                    probs[index] = (ushort)(prob - (prob >> kNumMoveBits));
                }
                while (_range < kTopValue) { _range <<= 8; ShiftLow(); }
            }

            private void EncodeDirectBits(uint v, int numBits)
            {
                for (int i = numBits - 1; i >= 0; i--)
                {
                    _range >>= 1;
                    if (((v >> i) & 1) != 0) _low += _range;
                    while (_range < kTopValue) { _range <<= 8; ShiftLow(); }
                }
            }

            private void Flush() { for (int i = 0; i < 5; i++) ShiftLow(); }

            // bit-tree (MSB first)
            private void EncodeBitTree(ushort[] probs, int numBits, int symbol)
            {
                int m = 1;
                for (int i = numBits - 1; i >= 0; i--)
                {
                    int bit = (symbol >> i) & 1;
                    EncodeBit(probs, m, bit);
                    m = (m << 1) | bit;
                }
            }

            // reverse bit-tree (LSB first), probs offset by 'ofs'
            private void EncodeBitTreeReverse(ushort[] probs, int ofs, int numBits, uint symbol)
            {
                int m = 1;
                for (int i = 0; i < numBits; i++)
                {
                    uint bit = symbol & 1; symbol >>= 1;
                    EncodeBit(probs, ofs + m, (int)bit);
                    m = (m << 1) | (int)bit;
                }
            }

            private void EncodeLiteral(int state, byte prevByte, byte symbol, byte[] data, int pos, uint rep0)
            {
                int litState = ((pos & ((1 << Lp) - 1)) << Lc) + (prevByte >> (8 - Lc));
                int baseIdx = litState * 0x300;
                EncodeLiteralCanonical(state, prevByte, symbol, data, pos, rep0, baseIdx);
            }

            // Literal coder: 8 plain bits, or (after a match) "matched" bits keyed on the byte one distance back
            // until the first divergence, then plain bits.
            private void EncodeLiteralCanonical(int state, byte prevByte, byte symbol, byte[] data, int pos, uint rep0, int baseIdx)
            {
                int context = 1;
                if (state >= 7)
                {
                    byte matchByte = data[pos - (int)rep0 - 1];
                    int bitPos = 7;
                    while (bitPos >= 0)
                    {
                        int matchBit = (matchByte >> bitPos) & 1;
                        int bit = (symbol >> bitPos) & 1;
                        EncodeBit(_literal, baseIdx + ((1 + matchBit) << 8) + context, bit);
                        context = (context << 1) | bit;
                        bitPos--;
                        if (matchBit != bit) break;
                    }
                    while (bitPos >= 0)
                    {
                        int bit = (symbol >> bitPos) & 1;
                        EncodeBit(_literal, baseIdx + context, bit);
                        context = (context << 1) | bit;
                        bitPos--;
                    }
                }
                else
                {
                    for (int bitPos = 7; bitPos >= 0; bitPos--)
                    {
                        int bit = (symbol >> bitPos) & 1;
                        EncodeBit(_literal, baseIdx + context, bit);
                        context = (context << 1) | bit;
                    }
                }
            }

            private void EncodeDistance(int len, uint dist)
            {
                int lenState = LenToPosState(len);
                int posSlot = PosSlot(dist);
                EncodeBitTree(_posSlot[lenState], kNumPosSlotBits, posSlot);

                if (posSlot >= kStartPosModelIndex)
                {
                    int footerBits = (posSlot >> 1) - 1;
                    uint baseVal = (uint)((2 | (posSlot & 1)) << footerBits);
                    if (posSlot < kEndPosModelIndex)
                    {
                        EncodeBitTreeReverse(_specPos, (int)baseVal - posSlot - 1, footerBits, dist - baseVal);
                    }
                    else
                    {
                        EncodeDirectBits((dist - baseVal) >> kNumAlignBits, footerBits - kNumAlignBits);
                        EncodeBitTreeReverse(_align, -1, kNumAlignBits, dist & ((1 << kNumAlignBits) - 1));
                    }
                }
            }

            // length encoder
            private sealed class LenEnc
            {
                private readonly ushort[] _choice = NewProbs(2);
                private readonly ushort[][] _low = JaggedP(1 << kNumPosBitsMax, kNumLenLow);
                private readonly ushort[][] _mid = JaggedP(1 << kNumPosBitsMax, kNumLenMid);
                private readonly ushort[] _high = NewProbs(kNumLenHigh);

                private static ushort[][] JaggedP(int a, int b)
                {
                    var r = new ushort[a][];
                    for (int i = 0; i < a; i++) r[i] = NewProbs(b);
                    return r;
                }

                public void Encode(Encoder e, int symbol, int posState)
                {
                    if (symbol < kNumLenLow)
                    {
                        e.EncodeBit(_choice, 0, 0);
                        e.EncodeBitTree(_low[posState], 3, symbol);
                    }
                    else
                    {
                        e.EncodeBit(_choice, 0, 1);
                        symbol -= kNumLenLow;
                        if (symbol < kNumLenMid)
                        {
                            e.EncodeBit(_choice, 1, 0);
                            e.EncodeBitTree(_mid[posState], 3, symbol);
                        }
                        else
                        {
                            e.EncodeBit(_choice, 1, 1);
                            e.EncodeBitTree(_high, 8, symbol - kNumLenMid);
                        }
                    }
                }
            }
        }

        // ===================== DECODER =====================
        private sealed class Decoder
        {
            private Stream _src;       // compressed input, read sequentially (range coder only reads forward)
            private uint _range = 0xFFFFFFFF;
            private uint _code;

            private byte Next() => (byte)_src.ReadByte();

            // --- sliding output window ---
            // A circular DictSize buffer is the back-reference dictionary AND the only output buffer: decoded bytes
            // are written here and flushed to the destination stream as the buffer fills, so peak memory is bounded
            // to the window (~DictSize) regardless of the uncompressed size — never the whole bundle at once. Valid
            // LZMA distances are < DictSize, so every back-reference still lives in the window. The buffer is a
            // pooled-decoder field, allocated once and reused across bundles.
            private byte[] _win;
            private int _winPos;        // next write index in the window
            private int _winStreamPos;  // window index up to which bytes have been flushed to _dst
            private Stream _dst;

            private void WinInit(Stream dst)
            {
                if (_win == null) _win = new byte[DictSize];
                _winPos = 0;
                _winStreamPos = 0;
                _dst = dst;
            }

            private void WinFlush()
            {
                int size = _winPos - _winStreamPos;
                if (size > 0) _dst.Write(_win, _winStreamPos, size);
                if (_winPos >= DictSize) _winPos = 0;
                _winStreamPos = _winPos;
            }

            private void WinPut(byte b)
            {
                _win[_winPos++] = b;
                if (_winPos >= DictSize) WinFlush();
            }

            private byte WinGet(int distance)
            {
                int i = _winPos - distance - 1;
                if (i < 0) i += DictSize;
                return _win[i];
            }

            private void WinCopyMatch(int distance, int len)
            {
                int i = _winPos - distance - 1;
                if (i < 0) i += DictSize;
                for (; len > 0; len--)
                {
                    if (i >= DictSize) i = 0;
                    byte b = _win[i++];
                    _win[_winPos++] = b;
                    if (_winPos >= DictSize) WinFlush();
                }
            }

            private readonly ushort[] _isMatch = NewProbs(kNumStates << kNumPosBitsMax);
            private readonly ushort[] _isRep = NewProbs(kNumStates);
            private readonly ushort[] _isRepG0 = NewProbs(kNumStates);
            private readonly ushort[] _isRepG1 = NewProbs(kNumStates);
            private readonly ushort[] _isRepG2 = NewProbs(kNumStates);
            private readonly ushort[] _isRep0Long = NewProbs(kNumStates << kNumPosBitsMax);
            private readonly ushort[][] _posSlot = JaggedD(kNumLenToPosStates, 1 << kNumPosSlotBits);
            private readonly ushort[] _specPos = NewProbs(kNumFullDistances - kEndPosModelIndex);
            private readonly ushort[] _align = NewProbs(1 << kNumAlignBits);
            private readonly ushort[] _literal = NewProbs((1 << (Lc + Lp)) * 0x300);
            private readonly LenDec _len = new LenDec();
            private readonly LenDec _repLen = new LenDec();

            private static ushort[][] JaggedD(int a, int b)
            {
                var r = new ushort[a][];
                for (int i = 0; i < a; i++) r[i] = NewProbs(b);
                return r;
            }

            // Reset all probability state to its initial value so a pooled decoder can be reused for the next
            // bundle without reallocating any of its model arrays.
            private void Reset()
            {
                Fill(_isMatch); Fill(_isRep); Fill(_isRepG0); Fill(_isRepG1); Fill(_isRepG2);
                Fill(_isRep0Long); foreach (var p in _posSlot) Fill(p);
                Fill(_specPos); Fill(_align); Fill(_literal);
                _len.Reset(); _repLen.Reset();
            }

            internal static void Fill(ushort[] a) { for (int i = 0; i < a.Length; i++) a[i] = kBitModelTotal >> 1; }

            // Reads the header + initializes the range decoder from the stream; returns the uncompressed length.
            // Resets first, so the instance is reusable from the pool.
            private int Begin(Stream src)
            {
                Reset();
                _src = src;
                _range = 0xFFFFFFFF;
                _code = 0;

                Next();              // props byte (lc/lp/pb fixed in this codec)
                for (int i = 0; i < 4; i++) Next(); // dict size (fixed window)
                ulong outLen = 0;
                for (int i = 0; i < 8; i++) outLen |= (ulong)Next() << (8 * i);

                Next();              // range init: skip one byte,
                for (int i = 0; i < 4; i++) _code = (_code << 8) | Next(); // then load four
                return checked((int)outLen);
            }

            /// <summary>Decodes from <paramref name="src"/>, streaming the output through the window into <paramref name="dst"/>.</summary>
            public void Decode(Stream src, Stream dst)
            {
                int total = Begin(src);
                Body(dst, total);
            }

            private void Body(Stream dst, int total)
            {
                WinInit(dst);
                int outPos = 0;

                int state = 0;
                uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
                int posMask = (1 << Pb) - 1;

                while (outPos < total)
                {
                    int posState = outPos & posMask;
                    if (DecodeBit(_isMatch, (state << kNumPosBitsMax) + posState) == 0)
                    {
                        byte prevByte = outPos > 0 ? WinGet(0) : (byte)0;
                        byte b = DecodeLiteral(state, prevByte, outPos, rep0);
                        WinPut(b);
                        outPos++;
                        state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                    }
                    else
                    {
                        int len;
                        if (DecodeBit(_isRep, state) == 1)
                        {
                            // rep match — present for format completeness (this encoder never emits these)
                            if (DecodeBit(_isRepG0, state) == 0)
                            {
                                if (DecodeBit(_isRep0Long, (state << kNumPosBitsMax) + posState) == 0)
                                {
                                    state = state < 7 ? 9 : 11;
                                    WinPut(WinGet((int)rep0));
                                    outPos++;
                                    continue;
                                }
                            }
                            else
                            {
                                uint dist;
                                if (DecodeBit(_isRepG1, state) == 0) { dist = rep1; }
                                else if (DecodeBit(_isRepG2, state) == 0) { dist = rep2; rep2 = rep1; }
                                else { dist = rep3; rep3 = rep2; rep2 = rep1; }
                                rep1 = rep0; rep0 = dist;
                            }
                            len = _repLen.Decode(this, posState) + kMatchMinLen;
                            state = state < 7 ? 8 : 11;
                        }
                        else
                        {
                            rep3 = rep2; rep2 = rep1; rep1 = rep0;
                            len = _len.Decode(this, posState) + kMatchMinLen;
                            state = state < 7 ? 7 : 10;
                            rep0 = DecodeDistance(len);
                        }

                        WinCopyMatch((int)rep0, len);
                        outPos += len;
                    }
                }

                WinFlush(); // flush the final partial window
            }

            // --- range decoder primitives ---
            private void Normalize() { if (_range < kTopValue) { _code = (_code << 8) | Next(); _range <<= 8; } }

            private int DecodeBit(ushort[] probs, int index)
            {
                uint prob = probs[index];
                uint bound = (_range >> kNumBitModelTotalBits) * prob;
                int symbol;
                if (_code < bound)
                {
                    _range = bound;
                    probs[index] = (ushort)(prob + ((kBitModelTotal - prob) >> kNumMoveBits));
                    symbol = 0;
                }
                else
                {
                    _code -= bound;
                    _range -= bound;
                    probs[index] = (ushort)(prob - (prob >> kNumMoveBits));
                    symbol = 1;
                }
                Normalize();
                return symbol;
            }

            private uint DecodeDirectBits(int numBits)
            {
                uint result = 0;
                for (int i = 0; i < numBits; i++)
                {
                    _range >>= 1;
                    uint t = (_code - _range) >> 31;
                    _code -= _range & (t - 1);
                    result = (result << 1) | (1 - t);
                    Normalize();
                }
                return result;
            }

            private int DecodeBitTree(ushort[] probs, int numBits)
            {
                int m = 1;
                for (int i = 0; i < numBits; i++) m = (m << 1) | DecodeBit(probs, m);
                return m - (1 << numBits);
            }

            private uint DecodeBitTreeReverse(ushort[] probs, int ofs, int numBits)
            {
                int m = 1; uint symbol = 0;
                for (int i = 0; i < numBits; i++)
                {
                    int bit = DecodeBit(probs, ofs + m);
                    m = (m << 1) | bit;
                    symbol |= (uint)bit << i;
                }
                return symbol;
            }

            private byte DecodeLiteral(int state, byte prevByte, int pos, uint rep0)
            {
                int litState = ((pos & ((1 << Lp) - 1)) << Lc) + (prevByte >> (8 - Lc));
                int baseIdx = litState * 0x300;
                int context = 1;

                if (state >= 7)
                {
                    byte matchByte = WinGet((int)rep0);
                    int bitPos = 7;
                    while (bitPos >= 0)
                    {
                        int matchBit = (matchByte >> bitPos) & 1;
                        int bit = DecodeBit(_literal, baseIdx + ((1 + matchBit) << 8) + context);
                        context = (context << 1) | bit;
                        bitPos--;
                        if (matchBit != bit) break;
                    }
                    while (bitPos >= 0)
                    {
                        context = (context << 1) | DecodeBit(_literal, baseIdx + context);
                        bitPos--;
                    }
                }
                else
                {
                    for (int bitPos = 7; bitPos >= 0; bitPos--)
                        context = (context << 1) | DecodeBit(_literal, baseIdx + context);
                }
                return (byte)(context & 0xFF);
            }

            private uint DecodeDistance(int len)
            {
                int lenState = LenToPosState(len);
                int posSlot = DecodeBitTree(_posSlot[lenState], kNumPosSlotBits);
                if (posSlot < kStartPosModelIndex) return (uint)posSlot;

                int footerBits = (posSlot >> 1) - 1;
                uint dist = (uint)((2 | (posSlot & 1)) << footerBits);
                if (posSlot < kEndPosModelIndex)
                    dist += DecodeBitTreeReverse(_specPos, (int)dist - posSlot - 1, footerBits);
                else
                {
                    dist += DecodeDirectBits(footerBits - kNumAlignBits) << kNumAlignBits;
                    dist += DecodeBitTreeReverse(_align, -1, kNumAlignBits);
                }
                return dist;
            }

            private sealed class LenDec
            {
                private readonly ushort[] _choice = NewProbs(2);
                private readonly ushort[][] _low = JaggedP(1 << kNumPosBitsMax, kNumLenLow);
                private readonly ushort[][] _mid = JaggedP(1 << kNumPosBitsMax, kNumLenMid);
                private readonly ushort[] _high = NewProbs(kNumLenHigh);

                private static ushort[][] JaggedP(int a, int b)
                {
                    var r = new ushort[a][];
                    for (int i = 0; i < a; i++) r[i] = NewProbs(b);
                    return r;
                }

                public void Reset()
                {
                    Fill(_choice);
                    foreach (var p in _low) Fill(p);
                    foreach (var p in _mid) Fill(p);
                    Fill(_high);
                }

                public int Decode(Decoder d, int posState)
                {
                    if (d.DecodeBit(_choice, 0) == 0) return d.DecodeBitTree(_low[posState], 3);
                    if (d.DecodeBit(_choice, 1) == 0) return kNumLenLow + d.DecodeBitTree(_mid[posState], 3);
                    return kNumLenLow + kNumLenMid + d.DecodeBitTree(_high, 8);
                }
            }
        }

        // ===================== MATCH FINDER (encoder) =====================
        // Greedy longest-match over a 3-byte hash chain. Correctness only requires that the (len,dist) it returns
        // is a real back-reference; the decoder reproduces it regardless of how it was chosen.
        private sealed class MatchFinder
        {
            private readonly byte[] _data;
            private readonly int[] _head;
            private readonly int[] _prev;
            private const int HashBits = 16;
            private const int HashSize = 1 << HashBits;
            private const int MaxChain = 128;

            public MatchFinder(byte[] data)
            {
                _data = data;
                _head = new int[HashSize];
                _prev = new int[data.Length + 1];
                for (int i = 0; i < HashSize; i++) _head[i] = -1;
            }

            private int Hash(int p)
            {
                if (p + 2 >= _data.Length) return -1;
                uint h = (uint)(_data[p] | (_data[p + 1] << 8) | (_data[p + 2] << 16));
                h = (h * 2654435761u) >> (32 - HashBits);
                return (int)h;
            }

            public void FindLongest(int pos, out int bestLen, out uint bestDist)
            {
                bestLen = 0; bestDist = 0;
                int h = Hash(pos);
                if (h < 0) return;

                int maxLen = Math.Min(kMatchMaxLen, _data.Length - pos);
                if (maxLen < kMatchMinLen) return;

                int cur = _head[h];
                int chain = MaxChain;
                int minPos = Math.Max(0, pos - DictSize);
                while (cur >= minPos && chain-- > 0)
                {
                    int len = 0;
                    while (len < maxLen && _data[cur + len] == _data[pos + len]) len++;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestDist = (uint)(pos - cur);
                        if (len == maxLen) break;
                    }
                    cur = _prev[cur];
                }
                if (bestLen < kMatchMinLen) { bestLen = 0; bestDist = 0; }
            }

            // Insert all positions the cursor advances over so later matches can reference them.
            public void Insert(int pos, int count)
            {
                int end = Math.Min(pos + count, _data.Length);
                for (int p = pos; p < end; p++)
                {
                    int h = Hash(p);
                    if (h < 0) continue;
                    _prev[p] = _head[h];
                    _head[h] = p;
                }
            }
        }
    }
}
