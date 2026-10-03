using EggEncoder.Transform;

namespace EggEncoder.Codecs.Tta
{
    // Decodes one TTA frame's compressed bytes into interleaved PCM samples. Scoped to mono and
    // stereo (16-bit) -- TTA's actual cross-channel decorrelation generalizes to N>2 channels via a
    // cascading per-sample difference across every channel (confirmed from ffmpeg's tta.c: for
    // channels>1, the last-decoded channel absorbs half of the second-to-last, then every channel
    // walks backward subtracting the next one), but that full generalization is out of scope here --
    // this implements exactly the N=2 case of that cascade (which is also the full case, not a
    // simplification of it).
    //
    // Unlike ALAC's channel-pair element (which codes one whole channel's worth of samples, then the
    // other's, sequentially), TTA interleaves per SAMPLE across channels -- confirmed from ffmpeg's
    // actual decode loop: channel 0's value at position i, channel 1's value at position i, then
    // decorrelate that one sample position, before moving to i+1. There is no escape/verbatim
    // fallback anywhere in TTA (its Rice coder's unary code is unbounded), so there is no
    // residual-overflow case to guard against here, unlike ALAC.
    internal static class TtaFrameDecoder
    {
        // ffmpeg's ff_tta_filter_configs[bytesPerSample-1] for 16-bit (2 bytes) is index 1 = 9.
        private const int FilterShift = 9;

        public static int[] DecodeFrame(BitReader reader, int channelCount, int sampleCount)
        {
            var channels = new TtaChannelState[channelCount];
            for (var c = 0; c < channelCount; c++)
            {
                channels[c] = new TtaChannelState(FilterShift);
            }

            var interleaved = new int[sampleCount * channelCount];

            if (channelCount == 1)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    interleaved[i] = channels[0].Decode(reader);
                }
            }
            else
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    var channel0 = channels[0].Decode(reader);
                    var channel1 = channels[1].Decode(reader);

                    var right = channel1 + (channel0 / 2);
                    var left = right - channel0;

                    interleaved[i * 2] = left;
                    interleaved[(i * 2) + 1] = right;
                }
            }

            return interleaved;
        }
    }
}
