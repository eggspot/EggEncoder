using EggEncoder.Codecs.Alac;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacFrameDecoderTest
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
        public void DecodePacket_VerbatimMode_Should_Read_RawSamples()
        {
            var samples = new[] { 1234, -5678, 0, short.MaxValue, short.MinValue };

            var writer = new BitWriter();
            writer.WriteBits(0, 3); // SCE tag
            writer.WriteBits(0, 4); // instance tag
            writer.WriteBits(0, 12); // unused
            writer.WriteBits(1, 1); // hasSize
            writer.WriteBits(0, 2); // extraBitsBytes
            writer.WriteBits(1, 1); // "not compressed" bit -- verbatim
            writer.WriteBits((uint)samples.Length, 32);
            writer.WriteBits(0, 8); // decorrShift
            writer.WriteBits(0, 8); // decorrLeftWeight

            foreach (var sample in samples)
            {
                writer.WriteBits((uint)sample & 0xFFFF, 16);
            }

            writer.WriteBits(7, 3); // END tag
            writer.ByteAlign();

            var decoded = AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            decoded.Should().Equal(samples);
        }

        [Fact]
        public void DecodePacket_Cpe_WithNonZeroDecorrLeftWeight_Should_Decorrelate()
        {
            // This encoder never produces a nonzero decorr_left_weight (see AlacFrameEncoder's doc
            // comment), but a real-world encoder's files can, and the decoder must still un-mix them
            // correctly. Channel 0/1's independently-decoded (pre-mix) streams here are [10,20,30] and
            // [5,15,25]; with shift=2, weight=4, ffmpeg's decorrelate_stereo (a -= (b*weight)>>shift;
            // b += a; swap outputs) hand-computes to final channel0=[10,20,30], channel1=[5,5,5] --
            // verified by hand before writing this test, not reverse-engineered from the code under test.
            var channel0 = new[] { 10, 20, 30 };
            var channel1 = new[] { 5, 15, 25 };

            var writer = new BitWriter();
            writer.WriteBits(1, 3); // CPE tag
            writer.WriteBits(0, 4); // instance tag
            writer.WriteBits(0, 12); // unused
            writer.WriteBits(1, 1); // hasSize
            writer.WriteBits(0, 2); // extraBitsBytes
            writer.WriteBits(1, 1); // "not compressed" bit -- verbatim, to isolate the decorrelation step
            writer.WriteBits(3, 32); // sampleCount
            writer.WriteBits(2, 8); // decorrShift
            writer.WriteBits(4, 8); // decorrLeftWeight

            foreach (var sample in channel0.Concat(channel1))
            {
                writer.WriteBits((uint)sample & 0xFFFF, 16);
            }

            writer.WriteBits(7, 3); // END tag
            writer.ByteAlign();

            var decoded = AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            decoded.Should().Equal(10, 5, 20, 5, 30, 5);
        }

        [Fact]
        public void DecodePacket_Cpe_WithZeroDecorrLeftWeight_Should_SkipDecorrelation()
        {
            var channel0 = new[] { 10, 20, 30 };
            var channel1 = new[] { 5, 15, 25 };

            var writer = new BitWriter();
            writer.WriteBits(1, 3); // CPE tag
            writer.WriteBits(0, 4);
            writer.WriteBits(0, 12);
            writer.WriteBits(1, 1); // hasSize
            writer.WriteBits(0, 2);
            writer.WriteBits(1, 1); // verbatim
            writer.WriteBits(3, 32); // sampleCount
            writer.WriteBits(9, 8); // decorrShift -- irrelevant, since weight is 0
            writer.WriteBits(0, 8); // decorrLeftWeight -- 0 means independent channels, no mixing at all

            foreach (var sample in channel0.Concat(channel1))
            {
                writer.WriteBits((uint)sample & 0xFFFF, 16);
            }

            writer.WriteBits(7, 3);
            writer.ByteAlign();

            var decoded = AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            decoded.Should().Equal(10, 5, 20, 15, 30, 25);
        }

        [Fact]
        public void DecodePacket_With_UnsupportedChannelElementTag_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(2, 3); // CCE tag -- not supported, only SCE (0) and CPE (1)
            writer.ByteAlign();

            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void DecodePacket_With_NonZeroExtraBits_Should_Throw()
        {
            var writer = new BitWriter();
            writer.WriteBits(0, 3); // SCE
            writer.WriteBits(0, 4);
            writer.WriteBits(0, 12);
            writer.WriteBits(0, 1); // hasSize
            writer.WriteBits(1, 2); // extraBitsBytes != 0
            writer.ByteAlign();

            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void DecodePacket_With_UnsupportedPredictionType_Should_Throw()
        {
            var writer = WriteCompressedHeader(sampleCount: 10, predictionType: 1, quantization: 6, order: 2);
            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void DecodePacket_With_ZeroQuantization_Should_Throw()
        {
            var writer = WriteCompressedHeader(sampleCount: 10, predictionType: 0, quantization: 0, order: 2);
            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void DecodePacket_With_PureDeltaPredictorOrder_Should_Throw()
        {
            var writer = WriteCompressedHeader(sampleCount: 10, predictionType: 0, quantization: 6, order: 31);
            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void DecodePacket_With_MissingEndTag_Should_Throw()
        {
            var samples = new[] { 1, 2, 3 };

            var writer = new BitWriter();
            writer.WriteBits(0, 3); // SCE
            writer.WriteBits(0, 4);
            writer.WriteBits(0, 12);
            writer.WriteBits(1, 1); // hasSize
            writer.WriteBits(0, 2);
            writer.WriteBits(1, 1); // verbatim, to keep this test simple
            writer.WriteBits((uint)samples.Length, 32);
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);

            foreach (var sample in samples)
            {
                writer.WriteBits((uint)sample & 0xFFFF, 16);
            }

            writer.WriteBits(0, 3); // wrong END tag (should be 7)
            writer.ByteAlign();

            var act = () => AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void DecodePacket_Without_ExplicitSize_Should_Use_ConfigFrameLength()
        {
            var samples = new int[Config.FrameLength];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 7;
            }

            var writer = new BitWriter();
            writer.WriteBits(0, 3); // SCE
            writer.WriteBits(0, 4);
            writer.WriteBits(0, 12);
            writer.WriteBits(0, 1); // hasSize = 0 -- sample count comes from config.FrameLength
            writer.WriteBits(0, 2);
            writer.WriteBits(1, 1); // verbatim, to keep this test simple
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);

            foreach (var sample in samples)
            {
                writer.WriteBits((uint)sample & 0xFFFF, 16);
            }

            writer.WriteBits(7, 3);
            writer.ByteAlign();

            var decoded = AlacFrameDecoder.DecodePacket(writer.ToArray(), Config);

            decoded.Should().Equal(samples);
        }

        private static BitWriter WriteCompressedHeader(int sampleCount, uint predictionType, uint quantization, uint order)
        {
            var writer = new BitWriter();
            writer.WriteBits(0, 3); // SCE
            writer.WriteBits(0, 4);
            writer.WriteBits(0, 12);
            writer.WriteBits(1, 1); // hasSize
            writer.WriteBits(0, 2);
            writer.WriteBits(0, 1); // compressed
            writer.WriteBits((uint)sampleCount, 32);
            writer.WriteBits(0, 8);
            writer.WriteBits(0, 8);
            writer.WriteBits(predictionType, 4);
            writer.WriteBits(quantization, 4);
            writer.WriteBits(4, 3); // riceHistoryMultiplier
            writer.WriteBits(order, 5);

            return writer;
        }
    }
}
