using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // Encodes one ALAC packet from one or two channels' worth of de-interleaved PCM samples, with a
    // fixed coefficient seed per channel and an explicit sample count.
    //
    // For two channels this always writes a channel-pair (CPE) element with decorrShift/
    // decorrLeftWeight both 0 -- i.e. always "independent channels", never the general affine
    // mid/side-style mixing ALAC's format supports. That's a legitimate, spec-correct simplification
    // (ffmpeg's own decoder skips the mixing step entirely whenever decorr_left_weight is 0, treating
    // the pair as two fully independent per-channel streams), the same kind of "correct but not
    // maximally compressed" shortcut as this encoder's fixed predictor seed -- not a hack. Decoding
    // arbitrary real-world files with nonzero mixing is still fully supported; see AlacFrameDecoder.
    //
    // Normally emits a compressed (predicted + Rice-coded) frame for each channel, falling back to
    // the verbatim (raw, uncompressed) encoding for the *whole element* (every channel, not just the
    // overflowing one -- isCompressed is one shared bit for a CPE, matching the bitstream layout) only
    // when some channel's residual doesn't fit the Rice coder's escape path -- see the isCompressed
    // comment in EncodePacket for why that's a real possibility, not just defensive paranoia.
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

        public static byte[] EncodePacket(int[][] channelSamples, int sampleCount, AlacSpecificConfig config)
        {
            var channelCount = channelSamples.Length;
            var predictionBitsPerSample = config.BitDepth + channelCount - 1;

            var order = Math.Min(PredictorOrder, sampleCount);
            var seedCoefficients = CoefficientSeed[..order];

            var channelResiduals = new int[channelCount][];
            var isCompressed = true;
            for (var c = 0; c < channelCount; c++)
            {
                var coefficients = (int[])seedCoefficients.Clone(); // Analyze mutates this; the bitstream must carry the pre-mutation seed
                var residuals = AlacLpcPredictor.Analyze(channelSamples[c], sampleCount, order, QuantizationShift, coefficients);
                channelResiduals[c] = residuals;

                // The Rice coder's "escape" fallback represents an out-of-range residual by writing
                // its zigzag-folded magnitude raw, in exactly predictionBitsPerSample bits -- so it
                // can only represent a residual that itself fits that signed range. The adaptive
                // predictor has no such bound (its coefficients can drift arbitrarily far from reality
                // against genuinely incompressible/adversarial input, e.g. uncorrelated random noise
                // -- real audio essentially never does this), so a residual can legitimately overflow
                // that escape. When it does for any channel, fall back to the verbatim encoding for
                // the whole element instead of silently producing a bitstream the escape code can't
                // actually hold. Real ALAC encoders have exactly this same verbatim fallback, for
                // exactly this reason.
                if (!residuals.All(r => FitsInSignedRange(r, predictionBitsPerSample)))
                {
                    isCompressed = false;
                }
            }

            var writer = new BitWriter();

            writer.WriteBits((uint)(channelCount == 1 ? ChannelElementSce : ChannelElementCpe), 3);
            writer.WriteBits(0, 4); // instance tag
            writer.WriteBits(0, 12); // unused
            writer.WriteBits(1, 1); // hasSize -- always write the true sample count
            writer.WriteBits(0, 2); // extraBitsBytes -- no wasted-bits support
            writer.WriteBits(isCompressed ? 0u : 1u, 1); // "not compressed" bit -- 0 means compressed
            writer.WriteBits((uint)sampleCount, 32);
            writer.WriteBits(0, 8); // decorrShift -- independent channels, no mixing
            writer.WriteBits(0, 8); // decorrLeftWeight -- independent channels, no mixing

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

                    AlacRiceCoder.EncodeResiduals(writer, channelResiduals[c], sampleCount, predictionBitsPerSample, config.Pb, config.Mb, config.Kb, RiceHistoryMultiplier);
                }
                else
                {
                    for (var i = 0; i < sampleCount; i++)
                    {
                        WriteSigned(writer, channelSamples[c][i], config.BitDepth);
                    }
                }
            }

            writer.WriteBits(ChannelElementEnd, 3);
            writer.ByteAlign();

            return writer.ToArray();
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
