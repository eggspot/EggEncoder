using EggEncoder.Codecs.Tta;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaFrameEncoderTest
    {
        [Fact]
        public void EncodeFrame_Then_DecodeFrame_Should_RoundTrip_Mono()
        {
            var samples = new int[4096];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(5000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
            }

            var frame = TtaFrameEncoder.EncodeFrame(samples, channelCount: 1, samples.Length);
            var decoded = TtaFrameDecoder.DecodeFrame(new BitReader(frame), channelCount: 1, samples.Length);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodeFrame_Then_DecodeFrame_Should_RoundTrip_Stereo_CorrelatedChannels()
        {
            var interleaved = new int[4096 * 2];
            for (var i = 0; i < 4096; i++)
            {
                var common = (int)(5000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
                interleaved[i * 2] = common + 100;
                interleaved[(i * 2) + 1] = common - 100;
            }

            var frame = TtaFrameEncoder.EncodeFrame(interleaved, channelCount: 2, sampleCount: 4096);
            var decoded = TtaFrameDecoder.DecodeFrame(new BitReader(frame), channelCount: 2, sampleCount: 4096);

            decoded.Should().Equal(interleaved);
        }

        [Fact]
        public void EncodeFrame_Then_DecodeFrame_Should_RoundTrip_Stereo_IndependentRandomChannels()
        {
            var random = new Random(55);
            var interleaved = new int[4096 * 2];
            for (var i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] = random.Next(-32768, 32768);
            }

            var frame = TtaFrameEncoder.EncodeFrame(interleaved, channelCount: 2, sampleCount: 4096);
            var decoded = TtaFrameDecoder.DecodeFrame(new BitReader(frame), channelCount: 2, sampleCount: 4096);

            decoded.Should().Equal(interleaved);
        }

        [Fact]
        public void EncodeFrame_Should_RoundTrip_A_SingleSampleFrame()
        {
            var samples = new[] { 12345 };

            var frame = TtaFrameEncoder.EncodeFrame(samples, channelCount: 1, sampleCount: 1);
            var decoded = TtaFrameDecoder.DecodeFrame(new BitReader(frame), channelCount: 1, sampleCount: 1);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodeFrame_Should_RoundTrip_An_EmptyFrame()
        {
            var frame = TtaFrameEncoder.EncodeFrame([], channelCount: 1, sampleCount: 0);
            var decoded = TtaFrameDecoder.DecodeFrame(new BitReader(frame), channelCount: 1, sampleCount: 0);

            decoded.Should().BeEmpty();
        }
    }
}
