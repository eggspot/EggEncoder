using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // Decodes one ALAC packet's bytes (one CAF 'data' chunk packet) into PCM samples. Scoped to mono
    // (single-channel SCE element) only -- stereo channel-pair elements use an additional mid/side-style
    // decorrelation step this doesn't implement, matching this tick's deliberately narrowed scope (see
    // AlacDecoder's doc comment).
    internal static class AlacFrameDecoder
    {
        private const int ChannelElementSce = 0;
        private const int ChannelElementEnd = 7;
        private const int PureDeltaPredictorOrder = 31;

        public static int[] DecodePacket(byte[] packetBytes, AlacSpecificConfig config)
        {
            var reader = new BitReader(packetBytes);

            var tag = reader.ReadBits(3);
            if (tag != ChannelElementSce)
            {
                throw new NotSupportedException($"ALAC channel element tag {tag} is not supported; only a single-channel (SCE, tag 0) element is supported");
            }

            reader.SkipBits(4); // instance tag
            reader.SkipBits(12); // unused

            var hasSize = reader.ReadBits(1) != 0;
            var extraBitsBytes = (int)reader.ReadBits(2);
            var isCompressed = reader.ReadBits(1) == 0;

            if (extraBitsBytes != 0)
            {
                throw new NotSupportedException("ALAC frames with extra/wasted bits are not supported");
            }

            var sampleCount = hasSize ? (int)reader.ReadBits(32) : config.FrameLength;

            reader.SkipBits(8); // decorrShift -- unused for a single channel
            reader.SkipBits(8); // decorrLeftWeight -- must be 0 for a single channel, unused either way

            var bitsPerSample = config.BitDepth;
            var samples = isCompressed
                ? DecodeCompressed(reader, sampleCount, bitsPerSample, config)
                : DecodeVerbatim(reader, sampleCount, bitsPerSample);

            var endTag = reader.ReadBits(3);
            if (endTag != ChannelElementEnd)
            {
                throw new InvalidDataException($"ALAC packet is missing its terminating element tag (expected {ChannelElementEnd}, got {endTag})");
            }

            return samples;
        }

        private static int[] DecodeCompressed(BitReader reader, int sampleCount, int bitsPerSample, AlacSpecificConfig config)
        {
            var predictionType = reader.ReadBits(4);
            if (predictionType != 0)
            {
                throw new NotSupportedException($"ALAC prediction type {predictionType} is not supported; only the standard dynamic predictor (type 0) is supported");
            }

            var quantization = (int)reader.ReadBits(4);
            if (quantization == 0)
            {
                throw new InvalidDataException("ALAC frame has a prediction quantization shift of 0, which is not a valid encoding");
            }

            var riceHistoryMultiplier = (int)reader.ReadBits(3);
            var order = (int)reader.ReadBits(5);

            if (order == PureDeltaPredictorOrder)
            {
                throw new NotSupportedException("The ALAC pure first-order-delta predictor (order 31) is not supported");
            }

            var coefficients = new int[order];
            for (var i = order - 1; i >= 0; i--)
            {
                coefficients[i] = ReadSigned(reader, 16);
            }

            var residuals = AlacRiceCoder.DecodeResiduals(reader, sampleCount, bitsPerSample, config.Pb, config.Mb, config.Kb, riceHistoryMultiplier);

            return AlacLpcPredictor.Reconstruct(residuals, sampleCount, order, quantization, coefficients);
        }

        private static int[] DecodeVerbatim(BitReader reader, int sampleCount, int bitsPerSample)
        {
            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = ReadSigned(reader, bitsPerSample);
            }

            return samples;
        }

        private static int ReadSigned(BitReader reader, int bitCount)
        {
            var value = reader.ReadBits(bitCount);
            var signBit = 1u << (bitCount - 1);

            return value >= signBit ? (int)(value - (signBit << 1)) : (int)value;
        }
    }
}
