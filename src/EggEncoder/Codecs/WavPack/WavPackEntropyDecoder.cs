namespace EggEncoder.Codecs.WavPack
{
    // Decodes WavPack's own residual entropy coding -- not Rice/Golomb coding, but an adaptive
    // scheme built around three per-channel "median" trackers (median[0..2], each estimating the
    // recent magnitude scale at an escalating band) plus a dedicated run-length shortcut for long
    // stretches of exact zero. One instance is shared by both channels of a stereo pair (its
    // "carry" bits and any in-progress zero run are genuinely shared state, consumed by whichever
    // channel's value is decoded next -- not independent per-channel state).
    internal sealed class WavPackEntropyDecoder
    {
        private readonly long[][] _medians; // [channel][0..2]
        private bool _carryOne;
        private bool _carryZero;
        private int _zeroesRemaining;

        // Once a zero run (shortcut or single escaped-zero) completes, the very next call must
        // decode normally without re-checking the zero-run gate at all -- even though the gate's
        // own condition (median[0] still near its post-run-reset low value, no pending carry) would
        // otherwise still be satisfied and misread that next sample's real class/tail/sign bits as
        // if they were a brand new zero-run escape code.
        private bool _justFinishedRun;

        public WavPackEntropyDecoder(int channels)
        {
            _medians = new long[channels][];
            for (var c = 0; c < channels; c++)
            {
                _medians[c] = new long[3];
            }
        }

        public void SeedMedian(int channel, int index, long value) => _medians[channel][index] = value;

        public int DecodeValue(WavPackBitReader reader, int channel)
        {
            var skipGate = _justFinishedRun;
            _justFinishedRun = false;

            if (!skipGate && AllChannelsBelowTwo() && !_carryOne && !_carryZero)
            {
                if (_zeroesRemaining > 0)
                {
                    _zeroesRemaining--;
                    _justFinishedRun = _zeroesRemaining == 0;
                    return 0;
                }

                var runLength = ReadEscapedUnary(reader);
                if (runLength > 0)
                {
                    _zeroesRemaining = runLength - 1;
                    foreach (var channelMedians in _medians)
                    {
                        channelMedians[0] = channelMedians[1] = channelMedians[2] = 0;
                    }

                    _justFinishedRun = _zeroesRemaining == 0;
                    return 0;
                }
            }

            var median = _medians[channel];

            // The class value's low bit is recycled as next call's "is the class definitely 0"
            // flag, so roughly every other call skips its own leading unary bit entirely.
            int t;
            if (_carryZero)
            {
                t = 0;
                _carryZero = false;
            }
            else
            {
                t = ReadUnary0To33(reader);
                if (t == 16)
                {
                    var extra = ReadUnary0To33(reader);
                    t += extra < 2 ? extra : ReadEscapedUnaryTail(reader, extra);
                }

                if (_carryOne)
                {
                    _carryOne = (t & 1) != 0;
                    t = (t >> 1) + 1;
                }
                else
                {
                    _carryOne = (t & 1) != 0;
                    t >>= 1;
                }

                _carryZero = !_carryOne;
            }

            long magnitude;
            int add;
            switch (t)
            {
                case 0:
                    magnitude = 0;
                    add = (int)Band(median[0]) - 1;
                    DecreaseMedian(median, 0);
                    break;
                case 1:
                    magnitude = Band(median[0]);
                    add = (int)Band(median[1]) - 1;
                    IncreaseMedian(median, 0);
                    DecreaseMedian(median, 1);
                    break;
                case 2:
                    magnitude = Band(median[0]) + Band(median[1]);
                    add = (int)Band(median[2]) - 1;
                    IncreaseMedian(median, 0);
                    IncreaseMedian(median, 1);
                    DecreaseMedian(median, 2);
                    break;
                default:
                    magnitude = Band(median[0]) + Band(median[1]) + (Band(median[2]) * (t - 2));
                    add = (int)Band(median[2]) - 1;
                    IncreaseMedian(median, 0);
                    IncreaseMedian(median, 1);
                    IncreaseMedian(median, 2);
                    break;
            }

            var tail = GetTail(reader, add);
            magnitude += tail;
            var sign = reader.ReadBit();
            return sign != 0 ? (int)(~magnitude) : (int)magnitude;
        }

        // The zero-run shortcut only applies once every channel's own short-term magnitude
        // estimate (median[0], the RAW tracked value, not the Band-expanded step size) has
        // decayed below 2 -- i.e. recent residuals have been consistently 0 or 1 in magnitude.
        private bool AllChannelsBelowTwo()
        {
            foreach (var median in _medians)
            {
                if (median[0] >= 2)
                {
                    return false;
                }
            }

            return true;
        }

        // median[n]>>4 + 1 is the "effective step size" derived from the raw (finer-grained)
        // tracked value -- always at least 1, which is what lets the tracker move at all starting
        // from a freshly-seeded (or reset) value of 0.
        private static long Band(long rawMedian) => (rawMedian >> 4) + 1;

        private static void IncreaseMedian(long[] median, int index)
        {
            var stepDenominator = 128 >> index;
            median[index] += 5 * ((median[index] + stepDenominator) / stepDenominator);
        }

        private static void DecreaseMedian(long[] median, int index)
        {
            var stepDenominator = 128 >> index;
            median[index] -= 2 * ((median[index] + stepDenominator - 2) / stepDenominator);
            if (median[index] < 0)
            {
                median[index] = 0;
            }
        }

        // Reads a capped unary code (a run of 1-bits terminated by a 0, capped at 33 ones with no
        // terminator needed at the cap).
        private static int ReadUnary0To33(WavPackBitReader reader)
        {
            var count = 0;
            while (count < 33 && reader.ReadBit() != 0)
            {
                count++;
            }

            return count;
        }

        // The zero-run length and the "class 16 overflow" case both extend an initial unary read
        // of 2 or more via the same minimal/truncated-binary escape: read (unary-1) raw bits X,
        // then the real value is X with an implicit leading 1 bit reinstated.
        private static int ReadEscapedUnary(WavPackBitReader reader)
        {
            var t = ReadUnary0To33(reader);
            return t < 2 ? t : ReadEscapedUnaryTail(reader, t);
        }

        private static int ReadEscapedUnaryTail(WavPackBitReader reader, int unaryValue)
        {
            var extraBits = unaryValue - 1;
            // A 32-bit int physically cannot represent "an implicit leading bit at position 31 or
            // 32" (C#'s shift operators mask the shift count to 0-31 besides, so `1 << 32` would
            // silently wrap to `1 << 0` rather than overflow or throw) -- reject outright rather
            // than silently producing a wrong value. No realistic 16/24-bit lossless WavPack
            // content ever needs a class index or zero-run length anywhere near this large.
            if (extraBits >= 31)
            {
                throw new InvalidDataException("A WavPack escaped-unary code's magnitude is too large to represent -- the file is corrupt.");
            }

            var x = (int)reader.ReadBits(extraBits);
            return x | (1 << extraBits);
        }

        // Decodes a value uniformly distributed over the k+1 possibilities [0, k] using a
        // truncated/minimal binary code: floor(log2(k)) bits are always read first, and only
        // values at or above the threshold where a (floor(log2(k))+1)-bit code is needed to
        // reach every one of the k+1 possibilities get one more bit appended -- rather than a
        // fixed-width code that would waste a fraction of a bit per value whenever k+1 isn't a
        // power of 2.
        private static int GetTail(WavPackBitReader reader, int k)
        {
            if (k < 1)
            {
                return 0;
            }

            var bitCount = BitLength(k) - 1;
            var threshold = (1 << (bitCount + 1)) - k - 1;

            var value = (int)reader.ReadBits(bitCount);
            if (value >= threshold)
            {
                value = (value * 2) - threshold + reader.ReadBit();
            }

            return value;
        }

        private static int BitLength(int value)
        {
            var bits = 0;
            while (value > 0)
            {
                bits++;
                value >>= 1;
            }

            return bits;
        }
    }
}
