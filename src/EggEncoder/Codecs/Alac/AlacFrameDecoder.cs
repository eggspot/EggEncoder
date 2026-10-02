using EggEncoder.Transform;

namespace EggEncoder.Codecs.Alac
{
    // Decodes one ALAC packet's bytes (one CAF 'data' chunk packet) into interleaved PCM samples.
    // Supports a single-channel SCE element (tag 0) and a two-channel CPE element (tag 1); anything
    // else (CCE/LFE/PCE/multi-pair streams) is out of scope.
    //
    // A CPE shares one element-level header with SCE (hasSize/extraBits/isCompressed/sampleCount),
    // adds an 8+8-bit decorrShift/decorrLeftWeight pair read once for the whole channel pair, then
    // runs the exact same per-channel prediction-header + residual decode as SCE twice in a row (one
    // call per channel, reusing DecodeCompressed/DecodeVerbatim unchanged). The two independently
    // reconstructed channels are then un-mixed (decorrelated) and interleaved.
    internal static class AlacFrameDecoder
    {
        private const int ChannelElementSce = 0;
        private const int ChannelElementCpe = 1;
        private const int ChannelElementEnd = 7;
        private const int PureDeltaPredictorOrder = 31;

        public static int[] DecodePacket(byte[] packetBytes, AlacSpecificConfig config)
        {
            var reader = new BitReader(packetBytes);

            var tag = reader.ReadBits(3);
            int channelCount;
            if (tag == ChannelElementSce)
            {
                channelCount = 1;
            }
            else if (tag == ChannelElementCpe)
            {
                channelCount = 2;
            }
            else
            {
                throw new NotSupportedException($"ALAC channel element tag {tag} is not supported; only single-channel (SCE, tag 0) and channel-pair (CPE, tag 1) elements are supported");
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

            var decorrShift = (int)reader.ReadBits(8);
            var decorrLeftWeight = (int)reader.ReadBits(8);

            var rawBitsPerSample = config.BitDepth;
            var predictionBitsPerSample = config.BitDepth + channelCount - 1;

            int[] interleavedSamples;
            if (channelCount == 1)
            {
                interleavedSamples = DecodeChannelBody(reader, sampleCount, rawBitsPerSample, predictionBitsPerSample, isCompressed, config);
            }
            else
            {
                var channel0 = DecodeChannelBody(reader, sampleCount, rawBitsPerSample, predictionBitsPerSample, isCompressed, config);
                var channel1 = DecodeChannelBody(reader, sampleCount, rawBitsPerSample, predictionBitsPerSample, isCompressed, config);

                if (decorrLeftWeight != 0)
                {
                    Decorrelate(channel0, channel1, sampleCount, decorrShift, decorrLeftWeight);
                }

                interleavedSamples = new int[sampleCount * 2];
                for (var i = 0; i < sampleCount; i++)
                {
                    interleavedSamples[i * 2] = channel0[i];
                    interleavedSamples[(i * 2) + 1] = channel1[i];
                }
            }

            var endTag = reader.ReadBits(3);
            if (endTag != ChannelElementEnd)
            {
                throw new InvalidDataException($"ALAC packet is missing its terminating element tag (expected {ChannelElementEnd}, got {endTag})");
            }

            return interleavedSamples;
        }

        // Reconstructs the true two channels from their independently-decoded forms in place.
        // channel0/channel1 arrive holding each channel's own LPC-reconstructed stream; on return
        // they hold the final (post-decorrelation) samples. Mirrors ffmpeg's decorrelate_stereo
        // exactly, including the channel0<->channel1 swap on the way out.
        private static void Decorrelate(int[] channel0, int[] channel1, int sampleCount, int decorrShift, int decorrLeftWeight)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var a = channel0[i];
                var b = channel1[i];
                a -= (b * decorrLeftWeight) >> decorrShift;
                b += a;
                channel0[i] = b;
                channel1[i] = a;
            }
        }

        private static int[] DecodeChannelBody(BitReader reader, int sampleCount, int rawBitsPerSample, int predictionBitsPerSample, bool isCompressed, AlacSpecificConfig config)
        {
            return isCompressed
                ? DecodeCompressed(reader, sampleCount, predictionBitsPerSample, config)
                : DecodeVerbatim(reader, sampleCount, rawBitsPerSample);
        }

        private static int[] DecodeCompressed(BitReader reader, int sampleCount, int predictionBitsPerSample, AlacSpecificConfig config)
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

            var residuals = AlacRiceCoder.DecodeResiduals(reader, sampleCount, predictionBitsPerSample, config.Pb, config.Mb, config.Kb, riceHistoryMultiplier);

            return AlacLpcPredictor.Reconstruct(residuals, sampleCount, order, quantization, coefficients);
        }

        private static int[] DecodeVerbatim(BitReader reader, int sampleCount, int rawBitsPerSample)
        {
            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = ReadSigned(reader, rawBitsPerSample);
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
