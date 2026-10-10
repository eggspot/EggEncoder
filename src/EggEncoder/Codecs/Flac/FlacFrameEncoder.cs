using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    // Encodes one FLAC frame per RFC 9639 sections 9.1-9.3: FIXED predictors only (orders 0-4), a
    // single Rice-coded partition per subframe, independent/left-side/right-side/mid-side stereo
    // mode chosen by exact cost comparison -- the FIXED-only MVP described in
    // docs/managed-codec-rewrite-plan.md item 2. True LPC search (item 3) is a follow-up; nothing
    // here produces anything other than CONSTANT/FIXED subframes.
    //
    // Every arithmetic step a FlacFrameDecoder-compatible decoder will reverse (the FIXED residual
    // formulas, stereo decorrelation) deliberately uses plain 32-bit `int` arithmetic, matching
    // FlacFrameDecoder's own RestoreFixedPrediction/Decorrelate exactly -- not because overflow
    // can't happen for extreme (near int32-range) input, but because two's-complement wraparound
    // addition/subtraction is its own exact inverse modulo 2^32, so encoding and decoding with the
    // *same* wraparound arithmetic round-trips exactly even when an intermediate sum "overflows".
    // Using wider (e.g. long) arithmetic here instead would silently break that round-trip for
    // pathological, extreme-range input.
    internal static class FlacFrameEncoder
    {
        private const int ChannelAssignmentLeftSide = 8;
        private const int ChannelAssignmentRightSide = 9;
        private const int ChannelAssignmentMidSide = 10;
        private const int MaxFixedOrder = 4;
        private const int MaxRiceParameter = 30; // residual coding method 1's 5-bit parameter field tops out at 30 (31 is the escape code)

        public static byte[] EncodeFrame(int[][] channelSamples, int sampleCount, int bitsPerSample, long frameIndex)
        {
            var channels = channelSamples.Length;
            int channelAssignment;
            int[][] codingSamples;
            int[] codingBitsPerSample;
            SubframePlan[] plans;

            // The side channel of a 32-bit stereo pair needs 33 bits to represent exactly (see
            // FlacFrameDecoder's own wide-side-channel path) -- this encoder has no 64-bit subframe
            // write path, so 32-bit stereo always falls back to independent coding, matching e.g.
            // AlacDecoder's own documented "a specific bit depth is out of scope" precedent rather
            // than silently producing a lossy or incorrect encode.
            if (channels == 2 && bitsPerSample < 32)
            {
                (channelAssignment, codingSamples, plans) = ChooseStereoMode(channelSamples[0], channelSamples[1], sampleCount, bitsPerSample);

                // Matches FlacFrameDecoder's own per-assignment channel ordering exactly: left/side
                // and mid/side both carry their un-flagged channel first and the (bitsPerSample+1)
                // side second, but right/side is the other way around (side first, "right" second).
                codingBitsPerSample = channelAssignment switch
                {
                    ChannelAssignmentLeftSide or ChannelAssignmentMidSide => [bitsPerSample, bitsPerSample + 1],
                    ChannelAssignmentRightSide => [bitsPerSample + 1, bitsPerSample],
                    _ => [bitsPerSample, bitsPerSample],
                };
            }
            else
            {
                channelAssignment = channels - 1;
                codingSamples = channelSamples;
                codingBitsPerSample = new int[channels];
                plans = new SubframePlan[channels];
                for (var c = 0; c < channels; c++)
                {
                    codingBitsPerSample[c] = bitsPerSample;
                    plans[c] = AnalyzeSubframe(channelSamples[c], bitsPerSample);
                }
            }

            var writer = new BitWriter();
            writer.WriteBits(0xFF, 8); // sync code, first 8 bits
            writer.WriteBits(0xF8, 8); // sync code remainder + reserved(0) + fixed-blocking-strategy(0) -- every frame but the last uses the same block size

            writer.WriteBits(7, 4); // block size code 7: explicit 16-bit value follows
            writer.WriteBits(0, 4); // sample rate code 0: get it from STREAMINFO

            writer.WriteBits((uint)channelAssignment, 4);
            writer.WriteBits(0, 3); // bits-per-sample code 0: get it from STREAMINFO
            writer.WriteBits(0, 1); // reserved

            WriteFrameNumber(writer, frameIndex);
            writer.WriteBits((uint)(sampleCount - 1), 16); // explicit block size (code 7)

            var headerBytes = writer.ToArray();
            writer.WriteBits(FlacCrc.Crc8(headerBytes, 0, headerBytes.Length), 8);

            for (var c = 0; c < channels; c++)
            {
                WriteSubframe(writer, plans[c], codingSamples[c], codingBitsPerSample[c]);
            }

            writer.ByteAlign();
            var preFooterBytes = writer.ToArray();
            writer.WriteBits(FlacCrc.Crc16(preFooterBytes, 0, preFooterBytes.Length), 16);

            return writer.ToArray();
        }

        // Tries every one of RFC 9639's four stereo modes (independent, left/side, right/side,
        // mid/side) and picks whichever has the smallest exact total cost -- mirrors this
        // codebase's own AlacFrameEncoder precedent of trying a mid-style mix and falling back,
        // except FLAC's own stereo decorrelation is always exactly invertible (no residual-overflow
        // fallback is ever needed the way ALAC's Rice-escape-range concern requires one).
        private static (int Assignment, int[][] Samples, SubframePlan[] Plans) ChooseStereoMode(int[] left, int[] right, int sampleCount, int bitsPerSample)
        {
            var mid = new int[sampleCount];
            var side = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                mid[i] = (left[i] + right[i]) >> 1;
                side[i] = left[i] - right[i];
            }

            var leftPlan = AnalyzeSubframe(left, bitsPerSample);
            var rightPlan = AnalyzeSubframe(right, bitsPerSample);
            var midPlan = AnalyzeSubframe(mid, bitsPerSample);
            var sidePlan = AnalyzeSubframe(side, bitsPerSample + 1);

            var independentCost = leftPlan.CostBits + rightPlan.CostBits;
            var leftSideCost = leftPlan.CostBits + sidePlan.CostBits;
            var rightSideCost = rightPlan.CostBits + sidePlan.CostBits;
            var midSideCost = midPlan.CostBits + sidePlan.CostBits;

            var cheapest = Math.Min(Math.Min(independentCost, leftSideCost), Math.Min(rightSideCost, midSideCost));

            if (cheapest == midSideCost)
            {
                return (ChannelAssignmentMidSide, [mid, side], [midPlan, sidePlan]);
            }

            if (cheapest == leftSideCost)
            {
                return (ChannelAssignmentLeftSide, [left, side], [leftPlan, sidePlan]);
            }

            if (cheapest == rightSideCost)
            {
                // Unlike left/side (where channel 0 is the un-flagged "left" and channel 1 is the
                // side, matching FlacFrameDecoder's own isSide/Decorrelate convention exactly),
                // right/side is the other way around: FlacFrameDecoder treats channel 0 as the side
                // and channel 1 as the un-flagged "right" for this specific assignment code.
                return (ChannelAssignmentRightSide, [side, right], [sidePlan, rightPlan]);
            }

            return (1, [left, right], [leftPlan, rightPlan]);
        }

        // ---------------------------------------------------------------- frame number (RFC 9639 section 9.1.5)

        // The UTF-8-style variable-length coding FlacFrameDecoder.SkipCodedNumber validates but
        // never uses the value of (STREAMINFO's own total-sample-count is its source of truth) --
        // so correctness here only requires a well-formed encoding, but writing the real frame
        // index keeps this encoder's output genuinely spec-compliant for any standards-compliant
        // decoder, not just this project's own.
        private static void WriteFrameNumber(BitWriter writer, long frameNumber)
        {
            if (frameNumber < 0x80)
            {
                writer.WriteBits((uint)frameNumber, 8);
                return;
            }

            // extraBytes continuation bytes each carry 6 payload bits; the lead byte carries
            // whatever's left (its own prefix bits are implicitly sized to make that exact) -- the
            // threshold on the right of each branch is 2^(leadBits + extraBytes*6).
            int extraBytes;
            uint leadPrefix;
            if (frameNumber < 0x800) // leadBits=5
            {
                extraBytes = 1;
                leadPrefix = 0xC0;
            }
            else if (frameNumber < 0x10000) // leadBits=4
            {
                extraBytes = 2;
                leadPrefix = 0xE0;
            }
            else if (frameNumber < 0x200000) // leadBits=3
            {
                extraBytes = 3;
                leadPrefix = 0xF0;
            }
            else if (frameNumber < 0x4000000) // leadBits=2
            {
                extraBytes = 4;
                leadPrefix = 0xF8;
            }
            else if (frameNumber < 0x80000000L) // leadBits=1
            {
                extraBytes = 5;
                leadPrefix = 0xFC;
            }
            else
            {
                throw new NotSupportedException($"Frame number {frameNumber} exceeds this encoder's supported range -- that's several billion blocks, far beyond any realistic file.");
            }

            var value = (uint)frameNumber;
            writer.WriteBits(leadPrefix | (value >> (extraBytes * 6)), 8);
            for (var i = extraBytes - 1; i >= 0; i--)
            {
                writer.WriteBits(0x80 | ((value >> (i * 6)) & 0x3Fu), 8);
            }
        }

        // ---------------------------------------------------------------- subframe analysis (choosing CONSTANT vs. the cheapest FIXED order)

        private readonly record struct SubframePlan(bool IsConstant, int Order, long CostBits, int[]? Residual, int RiceParameter, bool UseEscape, int EscapeWidth);

        private static SubframePlan AnalyzeSubframe(int[] samples, int bitsPerSample)
        {
            var sampleCount = samples.Length;
            var isConstant = true;
            for (var i = 1; i < sampleCount; i++)
            {
                if (samples[i] != samples[0])
                {
                    isConstant = false;
                    break;
                }
            }

            if (isConstant)
            {
                return new SubframePlan(true, 0, 8 + bitsPerSample, null, 0, false, 0);
            }

            SubframePlan? best = null;
            var maxOrder = Math.Min(MaxFixedOrder, sampleCount - 1);
            for (var order = 0; order <= maxOrder; order++)
            {
                var residual = ComputeFixedResidual(samples, order);
                var (residualCost, riceParameter, useEscape, escapeWidth) = ChooseResidualCoding(residual);
                var totalCost = 8L + ((long)order * bitsPerSample) + 6 /* residual method (2 bits) + partition order (4 bits) */ + residualCost;

                if (best is null || totalCost < best.Value.CostBits)
                {
                    best = new SubframePlan(false, order, totalCost, residual, riceParameter, useEscape, escapeWidth);
                }
            }

            return best!.Value;
        }

        // The k-th order FIXED predictor is, by RFC 9639's own definition, the k-th finite
        // difference of the signal -- these are the exact inverses of FlacFrameDecoder's own
        // RestoreFixedPrediction formulas (order, not coefficients, determines the shape).
        private static int[] ComputeFixedResidual(int[] samples, int order)
        {
            var residual = new int[samples.Length - order];
            switch (order)
            {
                case 0:
                    Array.Copy(samples, residual, residual.Length);
                    break;
                case 1:
                    for (var i = 1; i < samples.Length; i++)
                    {
                        residual[i - 1] = samples[i] - samples[i - 1];
                    }

                    break;
                case 2:
                    for (var i = 2; i < samples.Length; i++)
                    {
                        residual[i - 2] = samples[i] - (2 * samples[i - 1]) + samples[i - 2];
                    }

                    break;
                case 3:
                    for (var i = 3; i < samples.Length; i++)
                    {
                        residual[i - 3] = samples[i] - (3 * samples[i - 1]) + (3 * samples[i - 2]) - samples[i - 3];
                    }

                    break;
                default:
                    for (var i = 4; i < samples.Length; i++)
                    {
                        residual[i - 4] = samples[i] - (4 * samples[i - 1]) + (6 * samples[i - 2]) - (4 * samples[i - 3]) + samples[i - 4];
                    }

                    break;
            }

            return residual;
        }

        // ---------------------------------------------------------------- residual coding (single partition: method 1, partition order 0)

        // Exact bit cost (not an approximation) of Rice-coding this residual at parameter k is
        // n*(k+1) + sum(zigzag(r) >> k) -- the unary quotient plus its terminating 1, plus k
        // remainder bits, per value -- so a brute-force search over every valid k (0-30) finds the
        // true optimum directly, with no need to approximate it from the mean magnitude first.
        private static (long Cost, int RiceParameter, bool UseEscape, int EscapeWidth) ChooseResidualCoding(int[] residual)
        {
            var n = residual.Length;
            if (n == 0)
            {
                return (5, 0, false, 0);
            }

            var zigzag = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var v = residual[i];
                zigzag[i] = (uint)((v << 1) ^ (v >> 31));
            }

            var bestRiceCost = long.MaxValue;
            var bestParameter = 0;
            for (var k = 0; k <= MaxRiceParameter; k++)
            {
                var cost = (long)n * (k + 1);
                foreach (var z in zigzag)
                {
                    cost += z >> k;
                }

                if (cost < bestRiceCost)
                {
                    bestRiceCost = cost;
                    bestParameter = k;
                }
            }

            var escapeWidth = RequiredSignedWidth(residual);
            var escapeCost = 5L + ((long)n * escapeWidth);
            var riceCost = 5L + bestRiceCost;

            return escapeCost < riceCost ? (escapeCost, 0, true, escapeWidth) : (riceCost, bestParameter, false, 0);
        }

        // The minimal signed bit width every value in `residual` fits in -- 0 only when every value
        // is exactly 0 (RFC 9639's own "raw width 0 -> every value is 0" escape convention, which
        // FlacFrameDecoder's own escape-read path already relies on).
        private static int RequiredSignedWidth(int[] residual)
        {
            var allZero = true;
            var maxWidth = 0;
            foreach (var v in residual)
            {
                if (v == 0)
                {
                    continue;
                }

                allZero = false;
                var unsigned = v < 0 ? ~v : v;
                var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)unsigned);
                maxWidth = Math.Max(maxWidth, bits + 1);
            }

            return allZero ? 0 : maxWidth;
        }

        // ---------------------------------------------------------------- subframe writing

        private static void WriteSubframe(BitWriter writer, SubframePlan plan, int[] samples, int bitsPerSample)
        {
            if (plan.IsConstant)
            {
                writer.WriteBits(0, 8); // type 0 (CONSTANT), no wasted bits
                writer.WriteSignedBits(samples[0], bitsPerSample);
                return;
            }

            writer.WriteBits((uint)((8 + plan.Order) << 1), 8); // type 8+order (FIXED), no wasted bits

            for (var i = 0; i < plan.Order; i++)
            {
                writer.WriteSignedBits(samples[i], bitsPerSample);
            }

            writer.WriteBits(1, 2); // residual coding method 1 (5-bit Rice parameters)
            writer.WriteBits(0, 4); // partition order 0: a single partition

            if (plan.UseEscape)
            {
                writer.WriteBits(31, 5); // the method-1 escape code
                writer.WriteBits((uint)plan.EscapeWidth, 5);
                if (plan.EscapeWidth > 0)
                {
                    foreach (var r in plan.Residual!)
                    {
                        writer.WriteSignedBits(r, plan.EscapeWidth);
                    }
                }

                return;
            }

            writer.WriteBits((uint)plan.RiceParameter, 5);
            foreach (var r in plan.Residual!)
            {
                var zigzag = (uint)((r << 1) ^ (r >> 31));
                writer.WriteUnary(zigzag >> plan.RiceParameter);
                if (plan.RiceParameter > 0)
                {
                    writer.WriteBits(zigzag & ((1u << plan.RiceParameter) - 1), plan.RiceParameter);
                }
            }
        }
    }
}
