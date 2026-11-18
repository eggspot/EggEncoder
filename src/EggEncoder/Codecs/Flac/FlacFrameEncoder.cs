using EggEncoder.Transform;

namespace EggEncoder.Codecs.Flac
{
    // Encodes one FLAC frame per RFC 9639 sections 9.1-9.3: CONSTANT, FIXED (orders 0-4), and LPC
    // (orders 1-8, Levinson-Durbin coefficient estimation + error-feedback quantization) subframes,
    // a single Rice-coded partition per subframe, independent/left-side/right-side/mid-side stereo
    // mode -- all chosen by exact cost comparison, per docs/managed-codec-rewrite-plan.md items 2-3.
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
        private const int MaxLpcOrder = 8; // a modest order cap -- real encoders' lower compression levels use similar values; higher orders cost more to search for a shrinking return
        private const int LpcPrecision = 14; // fits the 4-bit precision code's own range (1-15) with headroom; fixed rather than searched, unlike order

        public static byte[] EncodeFrame(int[][] channelSamples, int sampleCount, int bitsPerSample, int sampleRate, long frameIndex)
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

            var writer = new BitWriter(64 + (channels * sampleCount * (bitsPerSample + 1) / 8));
            writer.WriteBits(0xFF, 8); // sync code, first 8 bits
            writer.WriteBits(0xF8, 8); // sync code remainder + reserved(0) + fixed-blocking-strategy(0) -- every frame but the last uses the same block size

            var blockSizeCode = FlacFrameHeader.BlockSizeCode(sampleCount);
            var sampleRateCode = FlacFrameHeader.SampleRateCode(sampleRate);
            writer.WriteBits((uint)blockSizeCode, 4);
            writer.WriteBits((uint)sampleRateCode, 4);

            writer.WriteBits((uint)channelAssignment, 4);
            writer.WriteBits((uint)FlacFrameHeader.SampleSizeCode(bitsPerSample), 3);
            writer.WriteBits(0, 1);

            WriteFrameNumber(writer, frameIndex);
            FlacFrameHeader.WriteBlockSizeTail(writer, blockSizeCode, sampleCount);
            FlacFrameHeader.WriteSampleRateTail(writer, sampleRateCode, sampleRate);

            var (headerBytes, headerLength) = writer.AlignedBytes();
            writer.WriteBits(FlacCrc.Crc8(headerBytes, 0, headerLength), 8);

            for (var c = 0; c < channels; c++)
            {
                WriteSubframe(writer, plans[c], codingSamples[c], codingBitsPerSample[c]);
            }

            writer.ByteAlign();
            var (frameBytes, frameLength) = writer.AlignedBytes();
            writer.WriteBits(FlacCrc.Crc16(frameBytes, 0, frameLength), 16);

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

            var (leftBits, leftSums) = EstimatedFixedBits(left);
            var (rightBits, rightSums) = EstimatedFixedBits(right);
            var (midBits, midSums) = EstimatedFixedBits(mid);
            var (sideBits, sideSums) = EstimatedFixedBits(side);

            var independentCost = leftBits + rightBits;
            var leftSideCost = leftBits + sideBits;
            var rightSideCost = rightBits + sideBits;
            var midSideCost = midBits + sideBits;

            var cheapest = Math.Min(Math.Min(independentCost, leftSideCost), Math.Min(rightSideCost, midSideCost));

            if (cheapest == midSideCost)
            {
                return (ChannelAssignmentMidSide, [mid, side], [AnalyzeSubframe(mid, bitsPerSample, midSums), AnalyzeSubframe(side, bitsPerSample + 1, sideSums)]);
            }

            if (cheapest == leftSideCost)
            {
                return (ChannelAssignmentLeftSide, [left, side], [AnalyzeSubframe(left, bitsPerSample, leftSums), AnalyzeSubframe(side, bitsPerSample + 1, sideSums)]);
            }

            if (cheapest == rightSideCost)
            {
                return (ChannelAssignmentRightSide, [side, right], [AnalyzeSubframe(side, bitsPerSample + 1, sideSums), AnalyzeSubframe(right, bitsPerSample, rightSums)]);
            }

            return (1, [left, right], [AnalyzeSubframe(left, bitsPerSample, leftSums), AnalyzeSubframe(right, bitsPerSample, rightSums)]);
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

        // ---------------------------------------------------------------- subframe analysis (choosing CONSTANT vs. the cheapest FIXED order vs. the cheapest LPC order)

        private enum SubframeKind
        {
            Constant,
            Verbatim,
            Fixed,
            Lpc,
        }

        private readonly record struct SubframePlan(SubframeKind Kind, int Order, long CostBits, int[]? Residual, int RiceParameter, int[]? Coefficients, int Shift);

        private static SubframePlan AnalyzeSubframe(int[] samples, int bitsPerSample, long[]? fixedSums = null)
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
                return new SubframePlan(SubframeKind.Constant, 0, 8 + bitsPerSample, null, 0, null, 0);
            }

            var fixedOrder = BestFixedOrderBySum(samples, Math.Min(MaxFixedOrder, sampleCount - 1), fixedSums);
            var fixedResidual = ComputeFixedResidual(samples, fixedOrder);
            var (fixedResidualCost, fixedRiceParameter) = ChooseResidualCoding(fixedResidual);
            var best = new SubframePlan(SubframeKind.Fixed, fixedOrder, 8L + ((long)fixedOrder * bitsPerSample) + 6 + fixedResidualCost, fixedResidual, fixedRiceParameter, null, 0);
            TryLpcOrders(samples, bitsPerSample, ref best);
            var verbatimCost = 8L + ((long)sampleCount * bitsPerSample);
            return verbatimCost < best.CostBits ? new SubframePlan(SubframeKind.Verbatim, 0, verbatimCost, null, 0, null, 0) : best;
        }

        // The k-th order FIXED predictor is, by RFC 9639's own definition, the k-th finite
        // difference of the signal -- these are the exact inverses of FlacFrameDecoder's own
        // RestoreFixedPrediction formulas (order, not coefficients, determines the shape).
        private static double[] AutocorrelationEightLags(double[] x)
        {
            if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
            {
                return AutocorrelationEightLagsTwoLanes(x);
            }

            var head = new double[MaxLpcOrder + 1];
            for (var i = 0; i < MaxLpcOrder; i++)
            {
                for (var lag = 0; lag <= i; lag++)
                {
                    head[lag] += x[i] * x[i - lag];
                }
            }

            double r0 = head[0], r1 = head[1], r2 = head[2], r3 = head[3], r4 = head[4], r5 = head[5], r6 = head[6], r7 = head[7], r8 = head[8];

            for (var i = MaxLpcOrder; i < x.Length; i++)
            {
                var value = x[i];
                r0 += value * value;
                r1 += value * x[i - 1];
                r2 += value * x[i - 2];
                r3 += value * x[i - 3];
                r4 += value * x[i - 4];
                r5 += value * x[i - 5];
                r6 += value * x[i - 6];
                r7 += value * x[i - 7];
                r8 += value * x[i - 8];
            }

            return [r0, r1, r2, r3, r4, r5, r6, r7, r8];
        }

        private static double[] AutocorrelationEightLagsTwoLanes(double[] x)
        {
            var lags = new double[MaxLpcOrder + 1];
            var start = MaxLpcOrder;
            for (var i = 0; i < start; i++)
            {
                for (var lag = 0; lag <= i; lag++)
                {
                    lags[lag] += x[i] * x[i - lag];
                }
            }

            ref var values = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(x);
            var r0 = System.Runtime.Intrinsics.Vector128<double>.Zero;
            var r1 = r0;
            var r2 = r0;
            var r3 = r0;
            var r4 = r0;
            var r5 = r0;
            var r6 = r0;
            var r7 = r0;
            var r8 = r0;
            var i0 = start;
            for (; i0 + 2 <= x.Length; i0 += 2)
            {
                var current = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)i0);
                r0 += current * current;
                r1 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 1));
                r2 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 2));
                r3 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 3));
                r4 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 4));
                r5 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 5));
                r6 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 6));
                r7 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 7));
                r8 += current * System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref values, (nuint)(i0 - 8));
            }

            System.Runtime.Intrinsics.Vector128<double>[] sums = [r0, r1, r2, r3, r4, r5, r6, r7, r8];
            for (var lag = 0; lag <= MaxLpcOrder; lag++)
            {
                lags[lag] += System.Runtime.Intrinsics.Vector128.GetElement(sums[lag], 0) + System.Runtime.Intrinsics.Vector128.GetElement(sums[lag], 1);
                for (var i = i0; i < x.Length; i++)
                {
                    lags[lag] += x[i] * x[i - lag];
                }
            }

            return lags;
        }

        private static int[] ComputeFixedResidual(int[] samples, int order)
        {
            var residual = new int[samples.Length - order];
            var start = FixedResidualInIntLanes(samples, order, residual);
            if (start > order)
            {
                for (var i = start; i < samples.Length; i++)
                {
                    residual[i - order] = order switch
                    {
                        0 => samples[i],
                        1 => samples[i] - samples[i - 1],
                        2 => samples[i] - (2 * samples[i - 1]) + samples[i - 2],
                        3 => samples[i] - (3 * samples[i - 1]) + (3 * samples[i - 2]) - samples[i - 3],
                        _ => samples[i] - (4 * samples[i - 1]) + (6 * samples[i - 2]) - (4 * samples[i - 3]) + samples[i - 4],
                    };
                }

                return residual;
            }

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

        // ---------------------------------------------------------------- LPC analysis (Levinson-Durbin + error-feedback quantization)

        // Tries every LPC order from 1 up to MaxLpcOrder (bounded by block size) and keeps `best`
        // updated with whichever of FIXED/LPC is cheapest so far -- same "try every order, compare
        // exact cost" structure as the FIXED loop in AnalyzeSubframe, just for a different predictor
        // family. A Welch window is applied before autocorrelation (confirmed, empirically, to
        // noticeably improve the resulting coefficients' predictive accuracy over an unwindowed,
        // effectively-rectangular-windowed autocorrelation estimate -- the latter's implicit sharp
        // edges bias the estimate, the classic motivation for windowing before any finite-block
        // spectral/autocorrelation estimate). This only affects which floating-point coefficients
        // get found and quantized, never correctness: whatever coefficients result, their *actual*
        // integer residual is what gets cost-compared and written, never the raw prediction-error
        // estimate itself.
        private static void TryLpcOrders(int[] samples, int bitsPerSample, ref SubframePlan best)
        {
            // AnalyzeSubframe only calls this after its own CONSTANT check has already excluded a
            // 1-sample (or otherwise genuinely constant) block, so sampleCount is always >=2 here --
            // meaning maxOrder is always >=1, with no separate guard needed for a case that can't
            // actually reach this method.
            var sampleCount = samples.Length;
            var maxOrder = Math.Min(MaxLpcOrder, sampleCount - 1);

            var windowed = ApplyWelchWindow(samples);
            var autocorrelation = ComputeAutocorrelation(windowed, maxOrder);
            if (autocorrelation[0] <= 0)
            {
                return; // a constant (already handled separately) or all-silent signal -- Levinson-Durbin's own division below would be by zero
            }

            var lpc = new double[maxOrder];
            var error = autocorrelation[0];
            var coefficientsByOrder = new double[maxOrder + 1][];
            var bestOrder = 0;
            var bestEstimate = double.MaxValue;

            for (var order = 1; order <= maxOrder; order++)
            {
                var acc = autocorrelation[order];
                for (var j = 0; j < order - 1; j++)
                {
                    acc -= lpc[j] * autocorrelation[order - 1 - j];
                }

                // No explicit guard against error<=0 here: mathematically it should stay positive
                // (it's a sum-of-squares, scaled down each step by a 1-k^2 factor that's also
                // meant to stay in [0,1]), and the only way it wouldn't is a floating-point
                // breakdown so narrow it isn't practical to construct a test input for. If it ever
                // did happen, the resulting reflection/coefficients would come out non-finite or
                // simply bad, and either QuantizeCoefficients' own NaN/Infinity check rejects them
                // outright, or they produce a residual too large to win the cost comparison below
                // -- so correctness never depends on catching this earlier.
                var reflection = acc / error;
                var updated = new double[order];
                for (var j = 0; j < order - 1; j++)
                {
                    updated[j] = lpc[j] - (reflection * lpc[order - 2 - j]);
                }

                updated[order - 1] = reflection;
                Array.Copy(updated, lpc, order);
                error *= 1 - (reflection * reflection);

                coefficientsByOrder[order] = lpc[..order];
                var estimate = (EstimatedBitsPerResidual(error, sampleCount) * (sampleCount - order)) + (order * (double)(LpcPrecision + bitsPerSample));
                if (estimate < bestEstimate)
                {
                    bestEstimate = estimate;
                    bestOrder = order;
                }
            }

            if (bestOrder == 0)
            {
                return;
            }

            var (coefficients, shift) = QuantizeCoefficients(coefficientsByOrder[bestOrder], LpcPrecision);
            if (coefficients is null)
            {
                return;
            }

            var values = bitsPerSample <= MaxExactDoubleLpcBits && FlacFrameDecoder.NeedsWideLpcAccumulator(bitsPerSample, bestOrder, LpcPrecision) ? ToDoubles(samples) : null;
            var residual = ComputeLpcResidual(samples, values, coefficients, shift, bitsPerSample);
            var (residualCost, riceParameter) = ChooseResidualCoding(residual);
            var totalCost = 8L + ((long)bestOrder * bitsPerSample) + 4 + 5 + ((long)bestOrder * LpcPrecision) + 6 + residualCost;
            if (totalCost < best.CostBits)
            {
                best = new SubframePlan(SubframeKind.Lpc, bestOrder, totalCost, residual, riceParameter, coefficients, shift);
            }
        }

        private static double EstimatedBitsPerResidual(double error, int sampleCount)
        {
            if (!(error > 0))
            {
                return 0;
            }

            var bits = 0.5 * Math.Log2(error * 0.5 / sampleCount);
            return bits > 0 ? bits : 0;
        }

        private const int RiceRunSteps = 16;

        private static int FixedSumRunSteps(int[] samples)
        {
            var largest = 0;
            var i = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var lanes = System.Numerics.Vector<int>.Count;
                var largestLanes = System.Numerics.Vector<int>.Zero;
                for (; i + lanes <= samples.Length; i += lanes)
                {
                    largestLanes = System.Numerics.Vector.Max(largestLanes, System.Numerics.Vector.Abs(new System.Numerics.Vector<int>(samples, i)));
                }

                for (var lane = 0; lane < lanes; lane++)
                {
                    largest = Math.Max(largest, largestLanes[lane]);
                }
            }

            for (; i < samples.Length; i++)
            {
                largest = Math.Max(largest, Math.Abs(samples[i]));
            }

            var largestDifference = 16L * largest;
            return largestDifference == 0 ? 1 << 20 : (int)Math.Max(1, uint.MaxValue / 2 / largestDifference);
        }

        private static (double Bits, long[]? Sums) EstimatedFixedBits(int[] samples)
        {
            if (samples.Length <= MaxFixedOrder)
            {
                return (samples.Length * 32.0, null);
            }

            var sums = FixedOrderSums(samples);
            var smallest = Math.Min(Math.Min(Math.Min(sums[0], sums[1]), Math.Min(sums[2], sums[3])), sums[4]);
            var count = samples.Length - MaxFixedOrder;
            return (smallest == 0 ? 0 : count * Math.Max(0, Math.Log2(Math.Log(2) * smallest / count)), sums);
        }

        private static int BestFixedOrderBySum(int[] samples, int maxOrder, long[]? knownSums)
        {
            if (samples.Length <= MaxFixedOrder)
            {
                return 0;
            }

            var sums = knownSums ?? FixedOrderSums(samples);
            var best = 0;
            for (var order = 1; order <= maxOrder; order++)
            {
                if (sums[order] < sums[best])
                {
                    best = order;
                }
            }

            return best;
        }

        private static long[] FixedOrderSums(int[] samples)
        {

            long sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0, sum4 = 0;
            var start = MaxFixedOrder;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var lanes = System.Numerics.Vector<int>.Count;
                var lanes0 = System.Numerics.Vector<long>.Zero;
                var lanes1 = System.Numerics.Vector<long>.Zero;
                var lanes2 = System.Numerics.Vector<long>.Zero;
                var lanes3 = System.Numerics.Vector<long>.Zero;
                var lanes4 = System.Numerics.Vector<long>.Zero;
                var runSteps = FixedSumRunSteps(samples);
                while (start + lanes <= samples.Length)
                {
                    var run0 = System.Numerics.Vector<uint>.Zero;
                    var run1 = System.Numerics.Vector<uint>.Zero;
                    var run2 = System.Numerics.Vector<uint>.Zero;
                    var run3 = System.Numerics.Vector<uint>.Zero;
                    var run4 = System.Numerics.Vector<uint>.Zero;
                    var end = Math.Min(samples.Length - lanes, start + ((runSteps - 1) * lanes));
                    for (; start <= end; start += lanes)
                    {
                    var s0 = new System.Numerics.Vector<int>(samples, start);
                    var s1 = new System.Numerics.Vector<int>(samples, start - 1);
                    var s2 = new System.Numerics.Vector<int>(samples, start - 2);
                    var s3 = new System.Numerics.Vector<int>(samples, start - 3);
                    var s4 = new System.Numerics.Vector<int>(samples, start - 4);
                    var e1 = s0 - s1;
                    var e2 = e1 - (s1 - s2);
                    var e3 = e2 - ((s1 - s2) - (s2 - s3));
                    var e4 = e3 - (((s1 - s2) - (s2 - s3)) - ((s2 - s3) - (s3 - s4)));
                    run0 += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(s0));
                    run1 += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(e1));
                    run2 += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(e2));
                    run3 += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(e3));
                    run4 += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(e4));
                    }

                    lanes0 += System.Numerics.Vector.AsVectorInt64(WidenedSum(run0));
                    lanes1 += System.Numerics.Vector.AsVectorInt64(WidenedSum(run1));
                    lanes2 += System.Numerics.Vector.AsVectorInt64(WidenedSum(run2));
                    lanes3 += System.Numerics.Vector.AsVectorInt64(WidenedSum(run3));
                    lanes4 += System.Numerics.Vector.AsVectorInt64(WidenedSum(run4));
                }

                sum0 = System.Numerics.Vector.Sum(lanes0);
                sum1 = System.Numerics.Vector.Sum(lanes1);
                sum2 = System.Numerics.Vector.Sum(lanes2);
                sum3 = System.Numerics.Vector.Sum(lanes3);
                sum4 = System.Numerics.Vector.Sum(lanes4);
            }

            var e0Previous = samples[start - 1];
            var e1Previous = samples[start - 1] - samples[start - 2];
            var e2Previous = e1Previous - (samples[start - 2] - samples[start - 3]);
            var e3Previous = e2Previous - ((samples[start - 2] - samples[start - 3]) - (samples[start - 3] - samples[start - 4]));
            for (var i = start; i < samples.Length; i++)
            {
                var e0 = samples[i];
                var e1 = e0 - e0Previous;
                var e2 = e1 - e1Previous;
                var e3 = e2 - e2Previous;
                var e4 = e3 - e3Previous;
                sum0 += Math.Abs((long)e0);
                sum1 += Math.Abs((long)e1);
                sum2 += Math.Abs((long)e2);
                sum3 += Math.Abs((long)e3);
                sum4 += Math.Abs((long)e4);
                e0Previous = e0;
                e1Previous = e1;
                e2Previous = e2;
                e3Previous = e3;
            }

            return [sum0, sum1, sum2, sum3, sum4];
        }

        [ThreadStatic]
        private static double[]? _welchWindow;


        private static double[] ApplyWelchWindow(int[] samples)
        {
            var n = samples.Length;
            var window = _welchWindow;
            if (window is null || window.Length != n)
            {
                window = new double[n];
                var half = (n - 1) / 2.0;
                for (var i = 0; i < n; i++)
                {
                    var t = (i - half) / half;
                    window[i] = 1 - (t * t);
                }

                _welchWindow = window;
            }

            var windowed = new double[n];
            for (var i = 0; i < n; i++)
            {
                windowed[i] = samples[i] * window[i];
            }

            return windowed;
        }

        private static double[] ComputeAutocorrelation(double[] samples, int maxLag)
        {
            if (maxLag == MaxLpcOrder && samples.Length > MaxLpcOrder)
            {
                return AutocorrelationEightLags(samples);
            }

            var r = new double[maxLag + 1];
            for (var lag = 0; lag <= maxLag; lag++)
            {
                var sum = 0.0;
                for (var i = lag; i < samples.Length; i++)
                {
                    sum += samples[i] * samples[i - lag];
                }

                r[lag] = sum;
            }

            return r;
        }

        // Quantizes floating-point LPC coefficients into `precision`-bit signed integers plus a
        // shift, using the standard error-feedback quantizer (each coefficient's own rounding error
        // carries forward into the next, rather than rounding each independently) -- a generic,
        // widely described DSP technique, not derived from any specific codec's implementation of
        // it. Returns null coefficients only when the input is degenerate (every coefficient 0, or
        // non-finite), in which case this LPC order simply isn't considered.
        private static (int[]? Coefficients, int Shift) QuantizeCoefficients(double[] lpc, int precision)
        {
            var maxAbs = 0.0;
            foreach (var c in lpc)
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(c));
            }

            if (maxAbs <= 0 || double.IsNaN(maxAbs) || double.IsInfinity(maxAbs))
            {
                return (null, 0);
            }

            // Choose the largest shift that keeps the largest-magnitude coefficient, once scaled,
            // inside the precision-bit signed range -- RFC 9639's own shift field is a 5-bit signed
            // value, but this encoder only ever writes a non-negative one (FlacDecoder rejects a
            // negative shift outright), so the result is also clamped to a sane non-negative range.
            var shift = Math.Clamp((precision - 2) - (int)Math.Floor(Math.Log2(maxAbs)), 0, 15);

            var limit = 1 << (precision - 1);
            var coefficients = new int[lpc.Length];
            var carriedError = 0.0;
            for (var i = 0; i < lpc.Length; i++)
            {
                var scaled = (lpc[i] * (1 << shift)) + carriedError;
                var quantized = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
                quantized = Math.Clamp(quantized, -limit, limit - 1);
                carriedError = scaled - quantized;
                coefficients[i] = quantized;
            }

            return (coefficients, shift);
        }

        // Mirrors FlacFrameDecoder.RestoreLpcPrediction/RestoreLpcPredictionWide exactly -- which
        // accumulator width is used is decided the same way FlacFrameDecoder.NeedsWideLpcAccumulator
        // decides it on decode, since a mismatch here would silently produce a residual the decoder
        // could never reconstruct the original samples from.
        private static int[] ComputeLpcResidual(int[] samples, double[]? values, int[] coefficients, int shift, int bitsPerSample)
        {
            var order = coefficients.Length;
            var residual = new int[samples.Length - order];

            if (FlacFrameDecoder.NeedsWideLpcAccumulator(bitsPerSample, order, LpcPrecision))
            {
                if (values is not null)
                {
                    LpcResidualInDoubles(samples, values, coefficients, shift, residual);
                    return residual;
                }

                for (var i = order; i < samples.Length; i++)
                {
                    var sum = 0L;
                    for (var j = 0; j < order; j++)
                    {
                        sum += (long)coefficients[j] * samples[i - 1 - j];
                    }

                    residual[i - order] = samples[i] - (int)(sum >> shift);
                }
            }
            else
            {
                var start = LpcResidualInIntLanes(samples, coefficients, shift, residual);
                for (var i = start; i < samples.Length; i++)
                {
                    var sum = 0;
                    for (var j = 0; j < order; j++)
                    {
                        sum += coefficients[j] * samples[i - 1 - j];
                    }

                    residual[i - order] = samples[i] - (sum >> shift);
                }
            }

            return residual;
        }

        // ---------------------------------------------------------------- residual coding (single partition: method 1, partition order 0)

        // Exact bit cost (not an approximation) of Rice-coding this residual at parameter k is
        // n*(k+1) + sum(zigzag(r) >> k) -- the unary quotient plus its terminating 1, plus k
        // remainder bits, per value -- so a brute-force search over every valid k (0-30) finds the
        // true optimum directly, with no need to approximate it from the mean magnitude first.
        private static (long Cost, int RiceParameter) ChooseResidualCoding(int[] residual)
        {
            var n = residual.Length;
            if (n == 0)
            {
                return (FourBitRiceParameterBits, 0);
            }

            long magnitudes = 0;
            long negatives = 0;
            var i = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var lanes = System.Numerics.Vector<int>.Count;
                var magnitudeLanes = System.Numerics.Vector<long>.Zero;
                var negativeLanes = System.Numerics.Vector<int>.Zero;
                while (i + lanes <= n)
                {
                    var run = System.Numerics.Vector<uint>.Zero;
                    var end = Math.Min(n - lanes, i + ((RiceRunSteps - 1) * lanes));
                    for (; i <= end; i += lanes)
                    {
                        var values = new System.Numerics.Vector<int>(residual, i);
                        run += System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Abs(values));
                        negativeLanes += System.Numerics.Vector.ShiftRightLogical(values, 31);
                    }

                    magnitudeLanes += System.Numerics.Vector.AsVectorInt64(WidenedSum(run));
                }

                magnitudes = System.Numerics.Vector.Sum(magnitudeLanes);
                negatives = System.Numerics.Vector.Sum(negativeLanes);
            }

            for (; i < n; i++)
            {
                magnitudes += Math.Abs((long)residual[i]);
                negatives += residual[i] < 0 ? 1 : 0;
            }

            var total = (2 * magnitudes) - negatives;
            var mean = total / n;
            var start = mean == 0 ? 0 : Math.Min(MaxRiceParameter, System.Numerics.BitOperations.Log2((ulong)mean));
            var bestParameter = start;
            var bestRiceCost = EstimatedRiceBits(total, n, start);
            if (start > 0 && EstimatedRiceBits(total, n, start - 1) is var lower && lower <= bestRiceCost)
            {
                bestParameter = start - 1;
                bestRiceCost = lower;
            }
            else if (start < MaxRiceParameter && EstimatedRiceBits(total, n, start + 1) is var higher && higher < bestRiceCost)
            {
                bestParameter = start + 1;
                bestRiceCost = higher;
            }

            return (RiceParameterBits(bestParameter) + bestRiceCost, bestParameter);
        }

        private static long EstimatedRiceBits(long total, int count, int parameter) =>
            ((long)count * (parameter + 1)) + (Math.Max(0, total - (count >> 1)) >> parameter);

        private const int MaxExactDoubleLpcBits = 25;
        private const double ExactIntegerBias = 6755399441055744.0;

        private static double[] ToDoubles(int[] samples)
        {
            var values = new double[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                values[i] = samples[i];
            }

            return values;
        }

        private static void LpcResidualInDoubles(int[] samples, double[] values, int[] coefficients, int shift, int[] residual)
        {
            var order = coefficients.Length;

            var scale = 1.0 / (1L << shift);
            var i0 = order;
            var lanes = System.Numerics.Vector<double>.Count;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var scales = new System.Numerics.Vector<double>(scale);
                var bias = new System.Numerics.Vector<double>(ExactIntegerBias);
                var biasBits = System.Numerics.Vector.AsVectorInt64(bias);
                for (; i0 + (2 * lanes) <= samples.Length; i0 += 2 * lanes)
                {
                    var low = System.Numerics.Vector<double>.Zero;
                    var high = System.Numerics.Vector<double>.Zero;
                    for (var j = 0; j < order; j++)
                    {
                        var weight = new System.Numerics.Vector<double>(coefficients[j]);
                        low += weight * new System.Numerics.Vector<double>(values, i0 - 1 - j);
                        high += weight * new System.Numerics.Vector<double>(values, i0 - 1 - j + lanes);
                    }

                    var lowResidual = new System.Numerics.Vector<double>(values, i0) - System.Numerics.Vector.Floor(low * scales);
                    var highResidual = new System.Numerics.Vector<double>(values, i0 + lanes) - System.Numerics.Vector.Floor(high * scales);
                    var lowBits = System.Numerics.Vector.AsVectorInt64(lowResidual + bias) - biasBits;
                    var highBits = System.Numerics.Vector.AsVectorInt64(highResidual + bias) - biasBits;
                    System.Numerics.Vector.Narrow(lowBits, highBits).CopyTo(residual, i0 - order);
                }
            }

            for (var i = i0; i < samples.Length; i++)
            {
                var sum = 0L;
                for (var j = 0; j < order; j++)
                {
                    sum += (long)coefficients[j] * samples[i - 1 - j];
                }

                residual[i - order] = samples[i] - (int)(sum >> shift);
            }
        }

        private static int LpcResidualInIntLanes(int[] samples, int[] coefficients, int shift, int[] residual)
        {
            var order = coefficients.Length;
            var i0 = order;
            if (!System.Numerics.Vector.IsHardwareAccelerated)
            {
                return i0;
            }

            var lanes = System.Numerics.Vector<int>.Count;
            for (; i0 + lanes <= samples.Length; i0 += lanes)
            {
                var sum = System.Numerics.Vector<int>.Zero;
                for (var j = 0; j < order; j++)
                {
                    sum += new System.Numerics.Vector<int>(coefficients[j]) * new System.Numerics.Vector<int>(samples, i0 - 1 - j);
                }

                (new System.Numerics.Vector<int>(samples, i0) - System.Numerics.Vector.ShiftRightArithmetic(sum, shift)).CopyTo(residual, i0 - order);
            }

            return i0;
        }

        private static int FixedResidualInIntLanes(int[] samples, int order, int[] residual)
        {
            if (!System.Numerics.Vector.IsHardwareAccelerated || order == 0)
            {
                return order;
            }

            var lanes = System.Numerics.Vector<int>.Count;
            var i = order;
            for (; i + lanes <= samples.Length; i += lanes)
            {
                var s0 = new System.Numerics.Vector<int>(samples, i);
                var s1 = new System.Numerics.Vector<int>(samples, i - 1);
                var value = order switch
                {
                    1 => s0 - s1,
                    2 => s0 - (s1 * 2) + new System.Numerics.Vector<int>(samples, i - 2),
                    3 => s0 - (s1 * 3) + (new System.Numerics.Vector<int>(samples, i - 2) * 3) - new System.Numerics.Vector<int>(samples, i - 3),
                    _ => s0 - (s1 * 4) + (new System.Numerics.Vector<int>(samples, i - 2) * 6) - (new System.Numerics.Vector<int>(samples, i - 3) * 4) + new System.Numerics.Vector<int>(samples, i - 4),
                };
                value.CopyTo(residual, i - order);
            }

            return i;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static System.Numerics.Vector<ulong> WidenedSum(System.Numerics.Vector<uint> values)
        {
            System.Numerics.Vector.Widen(values, out var low, out var high);
            return low + high;
        }

        private const uint VerbatimSubframeHeader = 0b0000_0010;
        private const int FourBitRiceParameterBits = 4;
        private const int MaxFourBitRiceParameter = 14;

        private static int RiceParameterBits(int parameter) => parameter <= MaxFourBitRiceParameter ? FourBitRiceParameterBits : 5;

        // ---------------------------------------------------------------- subframe writing

        private static void WriteSubframe(BitWriter writer, SubframePlan plan, int[] samples, int bitsPerSample)
        {
            if (plan.Kind == SubframeKind.Constant)
            {
                writer.WriteBits(0, 8); // type 0 (CONSTANT), no wasted bits
                writer.WriteSignedBits(samples[0], bitsPerSample);
                return;
            }

            if (plan.Kind == SubframeKind.Verbatim)
            {
                writer.WriteBits(VerbatimSubframeHeader, 8);
                foreach (var sample in samples)
                {
                    writer.WriteSignedBits(sample, bitsPerSample);
                }

                return;
            }

            // FIXED type codes are 8+order (RFC 9639 section 9.2.3); LPC type codes are 31+order
            // (section 9.2.4, i.e. 32 + (order-1)).
            var typeCode = plan.Kind == SubframeKind.Fixed ? 8 + plan.Order : 31 + plan.Order;
            writer.WriteBits((uint)(typeCode << 1), 8); // no wasted bits

            for (var i = 0; i < plan.Order; i++)
            {
                writer.WriteSignedBits(samples[i], bitsPerSample);
            }

            if (plan.Kind == SubframeKind.Lpc)
            {
                writer.WriteBits((uint)(LpcPrecision - 1), 4);
                writer.WriteSignedBits(plan.Shift, 5);
                foreach (var c in plan.Coefficients!)
                {
                    writer.WriteSignedBits(c, LpcPrecision);
                }
            }

            var parameterBits = RiceParameterBits(plan.RiceParameter);
            writer.WriteBits(parameterBits == FourBitRiceParameterBits ? 0u : 1u, 2);
            writer.WriteBits(0, 4);

            writer.WriteBits((uint)plan.RiceParameter, parameterBits);
            var parameter = plan.RiceParameter;
            var remainderMask = (1u << parameter) - 1;
            foreach (var r in plan.Residual!)
            {
                var zigzag = (uint)((r << 1) ^ (r >> 31));
                var quotient = zigzag >> parameter;
                if (quotient + 1 + parameter <= 32)
                {
                    writer.WriteBits((1u << parameter) | (zigzag & remainderMask), (int)quotient + 1 + parameter);
                    continue;
                }

                writer.WriteUnary(quotient);
                if (parameter > 0)
                {
                    writer.WriteBits(zigzag & remainderMask, parameter);
                }
            }
        }
    }
}
