using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // Encodes one ALAC packet (mono only -- see AlacFrameDecoder) from PCM samples, with a fixed
    // coefficient seed and an explicit sample count. Normally emits a compressed (predicted +
    // Rice-coded) frame, falling back to the verbatim (raw, uncompressed) frame encoding only when a
    // residual doesn't fit the Rice coder's escape path -- see the isCompressed comment in
    // EncodePacket for why that's a real possibility, not just defensive paranoia.
    internal static class AlacFrameEncoder
    {
        private const int ChannelElementSce = 0;
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

        public static byte[] EncodePacket(int[] samples, int sampleCount, AlacSpecificConfig config)
        {
            var writer = new BitWriter();

            writer.WriteBits(ChannelElementSce, 3);
            writer.WriteBits(0, 4); // instance tag
            writer.WriteBits(0, 12); // unused
            writer.WriteBits(1, 1); // hasSize -- always write the true sample count
            writer.WriteBits(0, 2); // extraBitsBytes -- no wasted-bits support

            var order = Math.Min(PredictorOrder, sampleCount);
            var seedCoefficients = CoefficientSeed[..order];
            var coefficients = (int[])seedCoefficients.Clone(); // Analyze mutates this; the bitstream must carry the pre-mutation seed
            var residuals = AlacLpcPredictor.Analyze(samples, sampleCount, order, QuantizationShift, coefficients);

            // The Rice coder's "escape" fallback represents an out-of-range residual by writing its
            // zigzag-folded magnitude raw, in exactly bitsPerSample (16) bits -- so it can only
            // represent a residual that itself fits in a signed 16-bit range. The adaptive predictor
            // has no such bound (its coefficients can drift arbitrarily far from reality against
            // genuinely incompressible/adversarial input, e.g. uncorrelated random noise -- real audio
            // essentially never does this), so a residual can legitimately overflow that escape. When
            // it does, fall back to the frame-level verbatim encoding instead of silently producing a
            // bitstream the escape code can't actually hold. Real ALAC encoders have exactly this same
            // verbatim fallback, for exactly this reason.
            var isCompressed = residuals.All(r => r is >= short.MinValue and <= short.MaxValue);

            writer.WriteBits(isCompressed ? 0u : 1u, 1); // "not compressed" bit -- 0 means compressed
            writer.WriteBits((uint)sampleCount, 32);
            writer.WriteBits(0, 8); // decorrShift -- unused for a single channel
            writer.WriteBits(0, 8); // decorrLeftWeight -- must be 0 for a single channel

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

                AlacRiceCoder.EncodeResiduals(writer, residuals, sampleCount, config.BitDepth, config.Pb, config.Mb, config.Kb, RiceHistoryMultiplier);
            }
            else
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    WriteSigned(writer, samples[i], config.BitDepth);
                }
            }

            writer.WriteBits(ChannelElementEnd, 3);
            writer.ByteAlign();

            return writer.ToArray();
        }

        private static void WriteSigned(BitWriter writer, int value, int bitCount)
        {
            var mask = bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1u;
            writer.WriteBits((uint)value & mask, bitCount);
        }
    }
}
