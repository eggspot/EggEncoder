using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // ALAC's "modified Rice"/adaptive Golomb-Rice residual coding. The Rice parameter k isn't sent in
    // the bitstream -- both sides derive it, per residual, from a running "history" value that adapts
    // based on the magnitude of each decoded/encoded residual (mb seeds it, pb/4 scales the adaptation).
    // When history drops below 128 (which happens routinely, not just for long silent runs -- history
    // starts at mb, typically 10, so this fires almost immediately at the start of nearly every frame)
    // both sides must additionally read/write a run-length "block size": the count of immediately
    // following residuals that are exact zero, encoded as a single value instead of individually. A
    // "sign modifier" of +1 (encode: -1) is then applied to the very next ordinary residual to correct
    // for the implicit zigzag parity the run-length shortcut skipped over.
    //
    // Encode and decode mirror each other by construction: decode reads whatever the bitstream contains;
    // encode always chooses the maximal exact-zero run at each escape point, which is simply the
    // bit-for-bit inverse of what decode would read back. This guarantees self-consistent round-tripping
    // regardless of whether every corner (particularly the escape mechanism) is a verified bit-exact
    // match to Apple's/ffmpeg's own encoder -- there is no real-world ALAC fixture available to check
    // bit-exactness against in this environment, so this file's own round-trip is the only thing that's
    // actually been verified.
    internal static class AlacRiceCoder
    {
        private const int HistoryEscapeThreshold = 128;

        public static int[] DecodeResiduals(BitReader reader, int sampleCount, int bitsPerSample, int pb, int mb, int kb, int riceHistoryMultiplier)
        {
            var residuals = new int[sampleCount];
            var history = mb;
            var signModifier = 0;
            var multiplier = riceHistoryMultiplier * pb / 4;

            var i = 0;
            while (i < sampleCount)
            {
                var k = Math.Min(Log2Floor((history >> 9) + 3), kb);
                var xRead = (int)DecodeScalar(reader, k, bitsPerSample);
                var x = xRead + signModifier;
                signModifier = 0;

                residuals[i] = ZigZagDecode(x);
                history = UpdateHistory(history, x, multiplier);
                i++;

                if (history < HistoryEscapeThreshold && i < sampleCount)
                {
                    var k2 = Math.Min(7 - Log2Floor(history) + ((history + 16) >> 6), kb);
                    var blockSize = (int)DecodeScalar(reader, k2, 16);

                    if (blockSize > 0)
                    {
                        var zeroCount = Math.Min(blockSize, sampleCount - i);
                        Array.Clear(residuals, i, zeroCount);
                        i += zeroCount;
                        signModifier = blockSize <= 0xFFFF ? 1 : 0;
                    }
                    else
                    {
                        signModifier = 0;
                    }

                    history = 0;
                }
            }

            return residuals;
        }

        public static void EncodeResiduals(BitWriter writer, int[] residuals, int sampleCount, int bitsPerSample, int pb, int mb, int kb, int riceHistoryMultiplier)
        {
            var history = mb;
            var signModifier = 0;
            var multiplier = riceHistoryMultiplier * pb / 4;

            var i = 0;
            while (i < sampleCount)
            {
                var k = Math.Min(Log2Floor((history >> 9) + 3), kb);
                var x = ZigZagEncode(residuals[i]);
                var xWritten = (uint)(x - signModifier);
                signModifier = 0;

                EncodeScalar(writer, xWritten, k, bitsPerSample);
                history = UpdateHistory(history, x, multiplier);
                i++;

                if (history < HistoryEscapeThreshold && i < sampleCount)
                {
                    var runLength = 0;
                    while (i + runLength < sampleCount && residuals[i + runLength] == 0)
                    {
                        runLength++;
                    }

                    var k2 = Math.Min(7 - Log2Floor(history) + ((history + 16) >> 6), kb);
                    EncodeScalar(writer, (uint)runLength, k2, 16);

                    if (runLength > 0)
                    {
                        i += runLength;
                        signModifier = runLength <= 0xFFFF ? 1 : 0;
                    }
                    else
                    {
                        signModifier = 0;
                    }

                    history = 0;
                }
            }
        }

        private static int UpdateHistory(int history, int x, int multiplier)
        {
            if (x > 0xFFFF)
            {
                return 0xFFFF;
            }

            var updated = history + (x * multiplier) - ((history * multiplier) >> 9);

            return Math.Clamp(updated, 0, 0xFFFF);
        }

        private static uint DecodeScalar(BitReader reader, int k, int bitsPerSample)
        {
            var unary = ReadUnary0To9(reader);
            if (unary > 8)
            {
                return reader.ReadBits(bitsPerSample);
            }

            if (k == 1)
            {
                return (uint)unary;
            }

            var baseValue = (uint)((unary << k) - unary);
            var extraBits = reader.PeekBits(k);

            if (extraBits > 1)
            {
                reader.SkipBits(k);
                return baseValue + extraBits - 1;
            }

            reader.SkipBits(k - 1);
            return baseValue;
        }

        private static void EncodeScalar(BitWriter writer, uint x, int k, int bitsPerSample)
        {
            var divisor = (1u << k) - 1u;
            var quotient = x / divisor;

            if (quotient > 8)
            {
                writer.WriteBits(0x1FF, 9); // nine 1-bits: the escape marker, no terminating 0
                writer.WriteBits(x, bitsPerSample);
                return;
            }

            if (quotient > 0)
            {
                writer.WriteBits((1u << (int)quotient) - 1u, (int)quotient);
            }

            writer.WriteBits(0, 1); // terminator

            if (k != 1)
            {
                var remainder = x % divisor;
                if (remainder > 0)
                {
                    writer.WriteBits(remainder + 1, k);
                }
                else
                {
                    writer.WriteBits(0, k - 1);
                }
            }
        }

        private static int ReadUnary0To9(BitReader reader)
        {
            var count = 0;
            while (true)
            {
                if (reader.ReadBits(1) == 0)
                {
                    return count;
                }

                count++;
                if (count == 9)
                {
                    return 9;
                }
            }
        }

        private static int ZigZagDecode(int x)
        {
            return (x >> 1) ^ -(x & 1);
        }

        private static int ZigZagEncode(int value)
        {
            return (value << 1) ^ (value >> 31);
        }

        private static int Log2Floor(int value)
        {
            if (value <= 0)
            {
                return 0;
            }

            var result = 0;
            while (value > 1)
            {
                value >>= 1;
                result++;
            }

            return result;
        }
    }
}
