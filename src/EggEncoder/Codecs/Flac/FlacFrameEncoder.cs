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

        // ---------------------------------------------------------------- subframe analysis (choosing CONSTANT vs. the cheapest FIXED order vs. the cheapest LPC order)

        private enum SubframeKind
        {
            Constant,
            Fixed,
            Lpc,
        }

        private readonly record struct SubframePlan(SubframeKind Kind, int Order, long CostBits, int[]? Residual, int RiceParameter, bool UseEscape, int EscapeWidth, int[]? Coefficients, int Shift);

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
                return new SubframePlan(SubframeKind.Constant, 0, 8 + bitsPerSample, null, 0, false, 0, null, 0);
            }

            SubframePlan? bestFixed = null;
            var maxFixedOrder = Math.Min(MaxFixedOrder, sampleCount - 1);
            for (var order = 0; order <= maxFixedOrder; order++)
            {
                var residual = ComputeFixedResidual(samples, order);
                var (residualCost, riceParameter, useEscape, escapeWidth) = ChooseResidualCoding(residual);
                var totalCost = 8L + ((long)order * bitsPerSample) + 6 /* residual method (2 bits) + partition order (4 bits) */ + residualCost;

                if (bestFixed is null || totalCost < bestFixed.Value.CostBits)
                {
                    bestFixed = new SubframePlan(SubframeKind.Fixed, order, totalCost, residual, riceParameter, useEscape, escapeWidth, null, 0);
                }
            }

            var best = bestFixed!.Value;
            TryLpcOrders(samples, bitsPerSample, ref best);
            return best;
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

                var (coefficients, shift) = QuantizeCoefficients(lpc[..order], LpcPrecision);
                if (coefficients is null)
                {
                    continue;
                }

                var residual = ComputeLpcResidual(samples, coefficients, shift, bitsPerSample);
                var (residualCost, riceParameter, useEscape, escapeWidth) = ChooseResidualCoding(residual);

                // 8 (subframe header) + order*bitsPerSample (warmup) + 4 (precision code) + 5
                // (shift) + order*LpcPrecision (coefficients) + 6 (residual method + partition
                // order) + the residual payload itself.
                var totalCost = 8L + ((long)order * bitsPerSample) + 4 + 5 + ((long)order * LpcPrecision) + 6 + residualCost;

                if (totalCost < best.CostBits)
                {
                    best = new SubframePlan(SubframeKind.Lpc, order, totalCost, residual, riceParameter, useEscape, escapeWidth, coefficients, shift);
                }
            }
        }

        // The classic Welch window: a simple parabola, 0 at both edges and 1 at the center --
        // cheap to compute and, unlike a rectangular (i.e. no) window, doesn't bias the
        // autocorrelation estimate with the sharp discontinuity a finite block's own hard edges
        // would otherwise introduce.
        private static double[] ApplyWelchWindow(int[] samples)
        {
            var n = samples.Length;
            var windowed = new double[n];
            var half = (n - 1) / 2.0;
            for (var i = 0; i < n; i++)
            {
                var t = (i - half) / half;
                windowed[i] = samples[i] * (1 - (t * t));
            }

            return windowed;
        }

        private static double[] ComputeAutocorrelation(double[] samples, int maxLag)
        {
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
        private static int[] ComputeLpcResidual(int[] samples, int[] coefficients, int shift, int bitsPerSample)
        {
            var order = coefficients.Length;
            var residual = new int[samples.Length - order];

            if (FlacFrameDecoder.NeedsWideLpcAccumulator(bitsPerSample, order, LpcPrecision))
            {
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
                for (var i = order; i < samples.Length; i++)
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
            if (plan.Kind == SubframeKind.Constant)
            {
                writer.WriteBits(0, 8); // type 0 (CONSTANT), no wasted bits
                writer.WriteSignedBits(samples[0], bitsPerSample);
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
