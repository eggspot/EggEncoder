using EggEncoder.Transform;

namespace EggEncoder.Codecs.Tta
{
    // Encodes one TTA frame from interleaved PCM samples. See TtaFrameDecoder's doc comment for scope
    // (mono/stereo 16-bit) and the per-sample (not per-channel-block) interleaving this mirrors.
    //
    // Unlike AlacFrameEncoder, there is nothing to choose here -- no predictor seed, no mixing
    // decision, no verbatim fallback. TTA's adaptive filter and Rice coder are both fully determined
    // by the input samples and the fixed per-frame-reset initial state; there is no parameter this
    // encoder picks that a real TTA encoder wouldn't also be forced into. The one piece that *is*
    // always applied (not a choice) is cross-channel decorrelation for stereo -- TTA has no
    // "independent channels" bitstream flag the way ALAC's CPE does, so this cannot skip it even if
    // it somehow made compression worse for some input (it structurally can't fail the way ALAC's
    // mixing could, since TTA's Rice coder has no bounded escape range to overflow).
    internal static class TtaFrameEncoder
    {
        private const int FilterShift = 9;

        public static byte[] EncodeFrame(int[] interleavedSamples, int channelCount, int sampleCount)
        {
            var channels = new TtaChannelState[channelCount];
            for (var c = 0; c < channelCount; c++)
            {
                channels[c] = new TtaChannelState(FilterShift);
            }

            var writer = new BitWriter();

            if (channelCount == 1)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    channels[0].Encode(writer, interleavedSamples[i]);
                }
            }
            else
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    var left = interleavedSamples[i * 2];
                    var right = interleavedSamples[(i * 2) + 1];

                    var channel0Coded = right - left;
                    var channel1Coded = right - (channel0Coded / 2);

                    channels[0].Encode(writer, channel0Coded);
                    channels[1].Encode(writer, channel1Coded);
                }
            }

            writer.ByteAlign();
            return writer.ToArray();
        }
    }
}
