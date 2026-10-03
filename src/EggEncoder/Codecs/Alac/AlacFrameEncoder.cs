using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // Encodes one ALAC packet from one or two channels' worth of de-interleaved PCM samples, with a
    // fixed coefficient seed per channel and an explicit sample count.
    //
    // For two channels this tries a fixed mid/side-style mix first (decorrShift=8, decorrLeftWeight=128,
    // i.e. a0 = right + (left-right)/2, b0 = left-right -- the classic mid/side split, expressed through
    // ALAC's general affine decorrelation) since real-world stereo audio is usually left/right-correlated
    // enough for this to compress meaningfully better than coding both channels independently. The
    // encode-side mix is the exact algebraic inverse of AlacFrameDecoder's un-mix step using the same
    // integer arithmetic, so it round-trips exactly for *any* weight/shift, including this one -- the
    // only thing that choice affects is compression ratio, never correctness. If the mixed channels'
    // residuals would overflow the Rice coder's escape range, this falls back to encoding the two
    // channels independently (decorrShift/decorrLeftWeight both 0, matching ALAC's "skip decorrelation
    // entirely" case -- see AlacFrameDecoder), and only after *that* still doesn't fit does it fall back
    // further to the verbatim (raw, uncompressed) encoding, writing the same (unmixed) samples that
    // fallback already settled on -- so verbatim mode is never asked to represent a mixed sample that
    // might need more than config.BitDepth bits, which is exactly the ambiguity this is designed to
    // dodge rather than resolve, given the real-world format has no fixture available here to check
    // confirm it against (this encoder's own round-trip is the thing that's actually been verified).
    //
    // Normally emits a compressed (predicted + Rice-coded) frame for each channel; isCompressed is one
    // shared bit for the whole element (SCE or CPE), matching the bitstream layout -- a residual
    // overflow in any one channel falls the whole frame back, not just that channel.
    internal static class AlacFrameEncoder
    {
        private const int ChannelElementSce = 0;
        private const int ChannelElementCpe = 1;
        private const int ChannelElementEnd = 7;

        // A fixed, reasonable-quality predictor seed (compression-level-1 fixed coefficients from the
        // reference encoder) rather than a per-frame Levinson-Durbin analysis -- the latter is a pure
        // compression-ratio optimization, not a correctness requirement: whatever seed is transmitted,
        // AlacLpcPredictor adapts it sample-by-sample within the frame, and the decoder reads back
        // whatever was actually sent rather than assuming this specific seed.
        private static readonly int[] CoefficientSeed = [160, -190, 170, -130, 80, -25];
        private const int PredictorOrder = 6;
        private const int QuantizationShift = 6;
        private const int RiceHistoryMultiplier = 4;

        // The classic mid/side split (weight/2^shift = 0.5), fixed rather than chosen per frame -- a
        // pure compression-ratio choice, same category of simplification as the fixed predictor seed.
        private const int MixShift = 8;
        private const int MixWeight = 128;

        public static byte[] EncodePacket(int[][] channelSamples, int sampleCount, AlacSpecificConfig config)
        {
            var channelCount = channelSamples.Length;
            var predictionBitsPerSample = config.BitDepth + channelCount - 1;
            var order = Math.Min(PredictorOrder, sampleCount);

            var decorrShift = 0;
            var decorrWeight = 0;
            int[][] codingSamples;
            int[][] residuals;
            bool isCompressed;

            if (channelCount == 2)
            {
                var mixed = Mix(channelSamples[0], channelSamples[1], sampleCount);
                var (mixedResiduals, mixedFits) = AnalyzeAllChannels(mixed, sampleCount, order, predictionBitsPerSample);

                if (mixedFits)
                {
                    codingSamples = mixed;
                    residuals = mixedResiduals;
                    isCompressed = true;
                    decorrShift = MixShift;
                    decorrWeight = MixWeight;
                }
                else
                {
                    codingSamples = channelSamples;
                    (residuals, isCompressed) = AnalyzeAllChannels(channelSamples, sampleCount, order, predictionBitsPerSample);
                }
            }
            else
            {
                codingSamples = channelSamples;
                (residuals, isCompressed) = AnalyzeAllChannels(channelSamples, sampleCount, order, predictionBitsPerSample);
            }

            var writer = new BitWriter();

            writer.WriteBits((uint)(channelCount == 1 ? ChannelElementSce : ChannelElementCpe), 3);
            writer.WriteBits(0, 4); // instance tag
            writer.WriteBits(0, 12); // unused
            writer.WriteBits(1, 1); // hasSize -- always write the true sample count
            writer.WriteBits(0, 2); // extraBitsBytes -- no wasted-bits support
            writer.WriteBits(isCompressed ? 0u : 1u, 1); // "not compressed" bit -- 0 means compressed
            writer.WriteBits((uint)sampleCount, 32);
            writer.WriteBits((uint)decorrShift, 8);
            writer.WriteBits((uint)decorrWeight, 8);

            var seedCoefficients = CoefficientSeed[..order];
            for (var c = 0; c < channelCount; c++)
            {
                if (isCompressed)
                {
                    writer.WriteBits(0, 4); // predictionType -- standard dynamic predictor
                    writer.WriteBits(QuantizationShift, 4);
                    writer.WriteBits(RiceHistoryMultiplier, 3);
                    writer.WriteBits((uint)order, 5);

                    for (var i = order - 1; i >= 0; i--)
                    {
                        WriteSigned(writer, seedCoefficients[i], 16);
                    }

                    AlacRiceCoder.EncodeResiduals(writer, residuals[c], sampleCount, predictionBitsPerSample, config.Pb, config.Mb, config.Kb, RiceHistoryMultiplier);
                }
                else
                {
                    for (var i = 0; i < sampleCount; i++)
                    {
                        WriteSigned(writer, codingSamples[c][i], config.BitDepth);
                    }
                }
            }

            writer.WriteBits(ChannelElementEnd, 3);
            writer.ByteAlign();

            return writer.ToArray();
        }

        // The exact algebraic inverse of AlacFrameDecoder.Decorrelate: given it computes
        // finalCh0 = b0 + a0 - ((b0*weight)>>shift) and finalCh1 = a0 - ((b0*weight)>>shift) from the
        // two independently-coded streams (a0, b0), solving for a0/b0 given finalCh0=left,
        // finalCh1=right gives b0 = left-right (no dependency on weight/shift at all) and
        // a0 = right + ((b0*weight)>>shift) -- using the same integer right-shift both directions
        // means the correction term cancels exactly on decode, for any weight/shift, with no rounding
        // error to accumulate.
        private static int[][] Mix(int[] left, int[] right, int sampleCount)
        {
            var a0 = new int[sampleCount];
            var b0 = new int[sampleCount];

            for (var i = 0; i < sampleCount; i++)
            {
                b0[i] = left[i] - right[i];
                a0[i] = right[i] + ((b0[i] * MixWeight) >> MixShift);
            }

            return [a0, b0];
        }

        private static (int[][] Residuals, bool Fits) AnalyzeAllChannels(int[][] samples, int sampleCount, int order, int predictionBitsPerSample)
        {
            var channelCount = samples.Length;
            var residuals = new int[channelCount][];
            var fits = true;

            for (var c = 0; c < channelCount; c++)
            {
                var coefficients = (int[])CoefficientSeed[..order].Clone(); // Analyze mutates this; the bitstream must carry the pre-mutation seed
                residuals[c] = AlacLpcPredictor.Analyze(samples[c], sampleCount, order, QuantizationShift, coefficients);

                // The Rice coder's "escape" fallback represents an out-of-range residual by writing
                // its zigzag-folded magnitude raw, in exactly predictionBitsPerSample bits -- so it
                // can only represent a residual that itself fits that signed range. The adaptive
                // predictor has no such bound (its coefficients can drift arbitrarily far from reality
                // against genuinely incompressible/adversarial input, e.g. uncorrelated random noise
                // -- real audio essentially never does this), so a residual can legitimately overflow
                // that escape. Real ALAC encoders have exactly this same verbatim fallback, for
                // exactly this reason.
                if (!residuals[c].All(r => FitsInSignedRange(r, predictionBitsPerSample)))
                {
                    fits = false;
                }
            }

            return (residuals, fits);
        }

        private static bool FitsInSignedRange(int value, int bitsPerSample)
        {
            var limit = 1 << (bitsPerSample - 1);
            return value >= -limit && value <= limit - 1;
        }

        private static void WriteSigned(BitWriter writer, int value, int bitCount)
        {
            // Only ever called with bitCount=16 (coefficients) or config.BitDepth, which this tick
            // always validates to 16 -- so a plain (1u<<bitCount)-1u mask is safe (it would overflow
            // at bitCount=32, but nothing here ever passes that).
            var mask = (1u << bitCount) - 1u;
            writer.WriteBits((uint)value & mask, bitCount);
        }
    }
}
