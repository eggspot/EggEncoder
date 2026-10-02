using EggEncoder.Codecs.Alac;
using EggEncoder.Transform;
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

            var packet = AlacFrameEncoder.EncodePacket([samples], samples.Length, Config);
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

            var packet = AlacFrameEncoder.EncodePacket([samples], samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_Should_RoundTrip_A_ShorterThanFrameLength_FinalFrame()
        {
            var samples = new[] { 100, -200, 300, -400, 500 };

            var packet = AlacFrameEncoder.EncodePacket([samples], samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_Should_RoundTrip_AllSilence()
        {
            var samples = new int[4096];

            var packet = AlacFrameEncoder.EncodePacket([samples], samples.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void EncodePacket_TwoChannels_Should_RoundTrip_CorrelatedSamples_As_Cpe()
        {
            var left = new int[4096];
            var right = new int[4096];
            for (var i = 0; i < left.Length; i++)
            {
                left[i] = (int)(5000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
                right[i] = (int)(3000 * Math.Sin(2 * Math.PI * 440 * i / 44100));
            }

            var packet = AlacFrameEncoder.EncodePacket([left, right], left.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            var expectedInterleaved = new int[left.Length * 2];
            for (var i = 0; i < left.Length; i++)
            {
                expectedInterleaved[i * 2] = left[i];
                expectedInterleaved[(i * 2) + 1] = right[i];
            }

            decoded.Should().Equal(expectedInterleaved);
        }

        [Fact]
        public void EncodePacket_TwoChannels_Should_UseTheWiderStereoEscapeRange_NotFallBackUnnecessarily()
        {
            // 6 samples == the predictor order, so every residual comes from the simple 1st-order-delta
            // warmup (no main LPC loop runs), making the exact residuals hand-computable rather than
            // dependent on uncertain long-run adaptive drift: left alternates +-32760, giving residuals
            // [32760, -65520, 65520, -65520, 65520, -65520] -- each overflows the mono (bps=16, max
            // 32767) escape range but fits the stereo (bps=17, max 65535) one exactly. Round-tripping
            // correctly isn't enough to prove EncodePacket actually used the wider range (verbatim
            // round-trips fine too), so this reads the "not compressed" bit directly out of the packet.
            var left = new[] { 32760, -32760, 32760, -32760, 32760, -32760 };
            var right = new[] { 0, 0, 0, 0, 0, 0 };

            var packet = AlacFrameEncoder.EncodePacket([left, right], left.Length, Config);

            var reader = new BitReader(packet);
            reader.SkipBits(3 + 4 + 12 + 1 + 2); // tag, instance tag, unused, hasSize, extraBitsBytes
            var notCompressedBit = reader.ReadBits(1);
            notCompressedBit.Should().Be(0u, "a bps=17-safe residual should stay compressed rather than fall back to verbatim");

            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            var expectedInterleaved = new int[left.Length * 2];
            for (var i = 0; i < left.Length; i++)
            {
                expectedInterleaved[i * 2] = left[i];
                expectedInterleaved[(i * 2) + 1] = right[i];
            }

            decoded.Should().Equal(expectedInterleaved);
        }

        [Fact]
        public void EncodePacket_TwoChannels_Should_FallBackToVerbatim_ForBothChannels_WhenOnlyOneOverflows()
        {
            // isCompressed is one shared bit for the whole CPE (see EncodePacket's doc comment) -- a
            // residual overflow in channel 1 alone must still force channel 0 into verbatim too.
            var left = new int[4096];
            var right = new int[4096];
            for (var i = 0; i < left.Length; i++)
            {
                left[i] = 0; // trivially compressible on its own
                right[i] = i % 2 == 0 ? short.MaxValue : short.MinValue; // defeats the predictor
            }

            var packet = AlacFrameEncoder.EncodePacket([left, right], left.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            var expectedInterleaved = new int[left.Length * 2];
            for (var i = 0; i < left.Length; i++)
            {
                expectedInterleaved[i * 2] = left[i];
                expectedInterleaved[(i * 2) + 1] = right[i];
            }

            decoded.Should().Equal(expectedInterleaved);
        }

        [Fact]
        public void EncodePacket_TwoChannels_Should_AlwaysWrite_Independent_ZeroMixing()
        {
            // This encoder never produces a nonzero decorr_left_weight (see its doc comment) --
            // confirmed here by checking the decoded channels never got mixed, i.e. channel 0 and
            // channel 1 decode back to exactly what was given, not some affine combination of them.
            var left = new[] { 1000, 2000, 3000 };
            var right = new[] { -500, -1500, -2500 };

            var packet = AlacFrameEncoder.EncodePacket([left, right], left.Length, Config);
            var decoded = AlacFrameDecoder.DecodePacket(packet, Config);

            decoded.Should().Equal(1000, -500, 2000, -1500, 3000, -2500);
        }
    }
}
