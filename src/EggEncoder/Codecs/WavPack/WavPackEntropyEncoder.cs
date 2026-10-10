namespace EggEncoder.Codecs.WavPack
{
    // Writes WavPack's own residual entropy coding -- the exact inverse of WavPackEntropyDecoder,
    // reusing its shared median math (Band/IncreaseMedian/DecreaseMedian) directly rather than
    // duplicating it. A block's full ordered sequence of (channel, residual) values is planned in
    // one forward pass (computing each value's class/tail/sign and evolving median state exactly
    // as decode would, since that evolution never depends on bit-level carry choices) and then
    // written in a second pass that decides the carry-enabling low bit for each class code using
    // one-symbol lookahead into the already-planned sequence.
    //
    // This encoder deliberately never emits a real zero-run (always writes the trivial "no run"
    // escape whenever the gating condition holds) -- correct and simple, just not yet exploiting
    // run-length compression for long silence, matching this project's established "MVP first,
    // correctness over compression ratio" precedent (see FlacEncoder's own items 2/3 history).
    internal sealed class WavPackEntropyEncoder
    {
        private readonly long[][] _medians;

        public WavPackEntropyEncoder(int channels)
        {
            _medians = new long[channels][];
            for (var c = 0; c < channels; c++)
            {
                _medians[c] = new long[3];
            }
        }

        // Starting every block cold (all medians at 0, matching WavPackEntropyDecoder's own
        // freshly-seeded state) is legal, but for content whose actual magnitude is nowhere near
        // the median's slow-adapting starting point, the first several values need an enormously
        // deep escaped-unary class code to express -- mathematically valid and something this
        // project's own decoder tolerates without complaint, but apparently outside what the real
        // reference wvunpack decoder considers a plausible WavPack stream (confirmed empirically:
        // it rejects such a file outright as "not compatible", even though every bit mirrors this
        // decoder's own already-verified read side exactly). A real encoder always measures its
        // block's own typical magnitude and seeds WP_ID_ENTROPY_VARS accordingly for exactly this
        // reason -- this mirrors that by seeding every tracker to the same caller-supplied starting
        // point instead of 0.
        public void SeedMedian(int channel, long value)
        {
            _medians[channel][0] = value;
            _medians[channel][1] = value;
            _medians[channel][2] = value;
        }

        public void Write(WavPackBitWriter writer, IReadOnlyList<(int Channel, int Value)> symbols)
        {
            var plans = new PlannedSymbol[symbols.Count];
            for (var i = 0; i < symbols.Count; i++)
            {
                var (channel, value) = symbols[i];
                var allBelowTwo = AllChannelsBelowTwo();
                var (rawClass, tail, add, sign) = PlanOne(_medians[channel], value);
                plans[i] = new PlannedSymbol(channel, allBelowTwo, rawClass, tail, add, sign);
            }

            var carryOne = false;
            var carryZero = false;
            for (var i = 0; i < plans.Length; i++)
            {
                var plan = plans[i];

                if (plan.AllChannelsBelowTwo && !carryOne && !carryZero)
                {
                    // This encoder never starts a real run -- always signal "no run" (a single
                    // escaped-unary value of 0, i.e. one 0-bit) so decode falls through to the
                    // normal class/tail/sign write immediately below, for every call.
                    writer.WriteUnary0To33(0);
                }

                if (carryZero)
                {
                    // The decoder will take its own free "class 0" shortcut here without reading
                    // any bits at all -- so this plan's own class MUST actually be 0 (guaranteed by
                    // only ever choosing to enable carryZero, below, when the next plan's class is
                    // 0), and nothing is written for the class code itself.
                    carryZero = false;
                }
                else
                {
                    var nextClassIsZero = i + 1 < plans.Length && plans[i + 1].TargetClass == 0;
                    var lowBit = nextClassIsZero ? 0 : 1;

                    long rawT = carryOne ? ((plan.TargetClass - 1) * 2) + lowBit : (plan.TargetClass * 2) + lowBit;
                    WriteRawUnaryValue(writer, rawT);

                    carryOne = lowBit != 0;
                    carryZero = !carryOne;
                }

                WriteTail(writer, plan.Tail, plan.Add);
                writer.WriteBit(plan.Sign);
            }
        }

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

        // Computes this value's class ("rawClass", before any carry adjustment -- i.e. exactly
        // what WavPackEntropyDecoder.DecodeValue's own `t` variable holds right after its carry
        // adjustment, since that's the class this plan ultimately needs the decoder to arrive at),
        // the tail/add (GetTail's own k parameter) needed to reach the exact magnitude, and the
        // sign bit -- advancing this channel's own median state exactly as decode's matching
        // switch(t) case would, so later values in the same block see the same evolving medians.
        private static (int TargetClass, int Tail, int Add, int Sign) PlanOne(long[] median, int value)
        {
            var sign = value < 0 ? 1 : 0;
            var magnitude = (long)(sign != 0 ? ~value : value);

            int rawClass;
            int add;
            if (magnitude < WavPackEntropyDecoder.Band(median[0]))
            {
                rawClass = 0;
                add = (int)WavPackEntropyDecoder.Band(median[0]) - 1;
                WavPackEntropyDecoder.DecreaseMedian(median, 0);
            }
            else if (magnitude < WavPackEntropyDecoder.Band(median[0]) + WavPackEntropyDecoder.Band(median[1]))
            {
                rawClass = 1;
                magnitude -= WavPackEntropyDecoder.Band(median[0]);
                add = (int)WavPackEntropyDecoder.Band(median[1]) - 1;
                WavPackEntropyDecoder.IncreaseMedian(median, 0);
                WavPackEntropyDecoder.DecreaseMedian(median, 1);
            }
            else
            {
                var remainder = magnitude - WavPackEntropyDecoder.Band(median[0]) - WavPackEntropyDecoder.Band(median[1]);
                var bandC = WavPackEntropyDecoder.Band(median[2]);
                add = (int)bandC - 1;
                WavPackEntropyDecoder.IncreaseMedian(median, 0);
                WavPackEntropyDecoder.IncreaseMedian(median, 1);

                if (remainder < bandC)
                {
                    // Exactly class 2 (quotient 0) is its own boundary case, decreasing median[2]
                    // -- mirroring classes 0/1's own "exact fit decreases, overflow increases"
                    // pattern -- not the "class > 2" increase every other value in this branch gets.
                    rawClass = 2;
                    magnitude = remainder;
                    WavPackEntropyDecoder.DecreaseMedian(median, 2);
                }
                else
                {
                    rawClass = 2 + (int)(remainder / bandC);
                    magnitude = remainder % bandC;
                    WavPackEntropyDecoder.IncreaseMedian(median, 2);
                }
            }

            return (rawClass, (int)magnitude, add, sign);
        }

        // The exact inverse of GetTail: writes bits such that WavPackEntropyDecoder's own GetTail
        // (given the same k) reads back exactly `tail`.
        private static void WriteTail(WavPackBitWriter writer, int tail, int k)
        {
            if (k < 1)
            {
                return;
            }

            var bitCount = BitLength(k) - 1;
            var threshold = (1 << (bitCount + 1)) - k - 1;

            if (tail < threshold)
            {
                writer.WriteBits((uint)tail, bitCount);
                return;
            }

            var d = tail - threshold;
            var value = threshold + (d >> 1);
            var extraBit = d & 1;
            writer.WriteBits((uint)value, bitCount);
            writer.WriteBit(extraBit);
        }

        // The exact inverse of the read side's `t = ReadUnary0To33(reader); if (t == 16) { var
        // extra = ReadUnary0To33(reader); t += extra < 2 ? extra : ReadEscapedUnaryTail(...); }`
        // -- writes whatever raw unary value (and, if needed, the 16-sentinel escape and its own
        // nested extension) reconstructs to exactly `rawT` when read back that same way. Values
        // 0-15 and 17-33 need no escape at all; 16 and anything above 33 always go through it (16
        // itself only representable via the escape, with a trivial zero-valued extension).
        private static void WriteRawUnaryValue(WavPackBitWriter writer, long rawT)
        {
            if (rawT is >= 0 and <= 33 and not 16)
            {
                writer.WriteUnary0To33((int)rawT);
                return;
            }

            writer.WriteUnary0To33(16);
            var extra = rawT - 16;
            if (extra < 2)
            {
                writer.WriteUnary0To33((int)extra);
            }
            else
            {
                var unaryValue = BitLength(extra);
                var extraBits = unaryValue - 1;
                var x = extra - (1L << extraBits);
                writer.WriteUnary0To33(unaryValue);
                writer.WriteBits((uint)x, extraBits);
            }
        }

        private static int BitLength(long value)
        {
            var bits = 0;
            while (value > 0)
            {
                bits++;
                value >>= 1;
            }

            return bits;
        }

        private readonly record struct PlannedSymbol(int Channel, bool AllChannelsBelowTwo, int TargetClass, int Tail, int Add, int Sign);
    }
}
