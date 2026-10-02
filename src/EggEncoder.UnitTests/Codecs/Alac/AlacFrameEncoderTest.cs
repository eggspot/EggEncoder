using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacFrameEncoderTest
    {
        private static readonly AlacSpecificConfig Config = new()
        {
            FrameLength = 4096,
            BitDepth = 16,
            Pb = 40,
            Mb = 10,
            Kb = 14,
            NumChannels = 1,
            MaxRun = 255,
            SampleRate = 44100
        };

        [Fact]
        public void EncodePacket_Then_DecodePacket_Should_RoundTrip_CorrelatedSamples()
        {
            var samples = new int[4096];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (int)(5000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
            }

            var packet = AlacFrameEncoder.EncodePacket(samples, samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_Should_FallBackToVerbatim_When_ResidualOverflowsTheEscapeCode()
        {
            // Full-scale, maximally-uncorrelated alternation defeats the adaptive predictor badly
            // enough to produce an out-of-16-bit-range residual -- EncodePacket must notice and switch
            // this frame to the verbatim encoding rather than silently truncating it. See
            // EncodePacket's isCompressed comment for why this is a real, reachable case.
            var samples = new int[4096];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 2 == 0 ? short.MaxValue : short.MinValue;
            }

            var packet = AlacFrameEncoder.EncodePacket(samples, samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_Should_RoundTrip_A_ShorterThanFrameLength_FinalFrame()
        {
            var samples = new[] { 100, -200, 300, -400, 500 };

            var packet = AlacFrameEncoder.EncodePacket(samples, samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_Should_RoundTrip_AllSilence()
        {
            var samples = new int[4096];

            var packet = AlacFrameEncoder.EncodePacket(samples, samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }
    }
}
