using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class MsAdpcmEncoderTest
    {
        [Fact]
        public void EncodeBlock_Mono_Should_Produce_A_Block_DecodeBlock_Can_Read_Back_Verbatim_First_Two_Samples()
        {
            const int samplesPerBlock = 10;
            var samples = new[] { 50, 100, 180, 260, 340, 420, 500, 580, 660, 740 };
            var encodeStates = new MsAdpcmDecoder.ChannelState[1];
            var block = new byte[7 + ((samplesPerBlock - 2) / 2)];

            MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock, encodeStates, block);

            var decodeStates = new MsAdpcmDecoder.ChannelState[1];
            var decoded = new int[samplesPerBlock];
            MsAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock, decodeStates, MsAdpcmEncoder.Coeff1Table, MsAdpcmEncoder.Coeff2Table, decoded);

            decoded[0].Should().Be(50, "frame 0 is the header's own sample2, written verbatim");
            decoded[1].Should().Be(100, "frame 1 is the header's own sample1, written verbatim");
            for (var i = 2; i < samplesPerBlock; i++)
            {
                Math.Abs(decoded[i] - samples[i]).Should().BeLessThan(50, $"sample {i} should reconstruct within a reasonable quantization tolerance");
            }
        }

        [Fact]
        public void EncodeBlock_Stereo_Should_Keep_Channels_Independent()
        {
            // Two genuinely different per-channel signals -- confirms EncodeBlock's own per-channel
            // header layout (grouped by field, not by channel) and ChannelState indexing don't
            // cross-contaminate.
            const int samplesPerBlock = 10;
            var samples = new[]
            {
                1000, 5000, // frame 0: ch0, ch1
                1010, 4980,
                1020, 4960,
                1030, 4940,
                1040, 4920,
                1050, 4900,
                1060, 4880,
                1070, 4860,
                1080, 4840,
                1090, 4820
            };
            var encodeStates = new MsAdpcmDecoder.ChannelState[2];
            var block = new byte[14 + (samplesPerBlock - 2)]; // stereo: one byte per FRAME (both channels' nibbles packed together), not per channel

            MsAdpcmEncoder.EncodeBlock(samples, channels: 2, samplesPerBlock, encodeStates, block);

            var decodeStates = new MsAdpcmDecoder.ChannelState[2];
            var decoded = new int[samplesPerBlock * 2];
            MsAdpcmDecoder.DecodeBlock(block, channels: 2, samplesPerBlock, decodeStates, MsAdpcmEncoder.Coeff1Table, MsAdpcmEncoder.Coeff2Table, decoded);

            decoded[0].Should().Be(1000, "frame 0, channel 0 is its own raw header sample2");
            decoded[1].Should().Be(5000, "frame 0, channel 1 is its own raw header sample2");
            decoded[2].Should().Be(1010, "frame 1, channel 0 is its own raw header sample1");
            decoded[3].Should().Be(4980, "frame 1, channel 1 is its own raw header sample1");
            for (var frame = 2; frame < samplesPerBlock; frame++)
            {
                Math.Abs(decoded[(frame * 2) + 0] - samples[(frame * 2) + 0]).Should().BeLessThan(50, $"frame {frame}, channel 0");
                Math.Abs(decoded[(frame * 2) + 1] - samples[(frame * 2) + 1]).Should().BeLessThan(50, $"frame {frame}, channel 1");
            }
        }

        [Fact]
        public void EncodeBlock_BlockPredictorByte_Should_Always_Be_Zero()
        {
            // Confirmed from FFmpeg's own real adpcmenc.c source (see this file's own doc comment):
            // its production encoder never searches the other 6 standard coefficient pairs, always
            // selecting index 0. This test exists so a future change that accidentally starts writing
            // a different index doesn't silently drift from that verified, real-world behavior.
            var samples = new[] { 100, 110, 120, 130, 140, 150, 160, 170, 180, 190 };
            var states = new MsAdpcmDecoder.ChannelState[1];
            var block = new byte[7 + 4];

            MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 10, states, block);

            block[0].Should().Be(0);
        }

        [Fact]
        public void EncodeBlock_HeaderDelta_Should_Reflect_The_CallerSupplied_Starting_Value_Not_This_Blocks_Own_Ending_One()
        {
            // Mirrors ImaAdpcmEncoderTest's own analogous finding for IMA ADPCM's step index: the
            // header's own delta bytes are written before any of this call's own encoding happens
            // (confirmed by reading EncodeBlock's own source, not assumed) -- they reflect whatever
            // delta carried over from the END of the PREVIOUS block (the caller-supplied starting
            // state), matching FFmpeg's own real adpcmenc.c (idelta is written to the header, then
            // used and adapted by that block's own nibble loop afterward).
            var states = new[] { new MsAdpcmDecoder.ChannelState { Sample1 = 500, Sample2 = 500, Delta = 200 } };
            var samples = new[] { 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080, 1090 };
            var block = new byte[7 + 4];

            MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 10, states, block);

            var writtenDelta = (short)(block[1] | (block[2] << 8));
            writtenDelta.Should().Be(200, "the header's own delta bytes must reflect the state carried in from the end of the previous block");
        }

        [Fact]
        public void EncodeBlock_HeaderDelta_Should_Clamp_Up_To_The_Floor_Of_Sixteen_If_Below_It()
        {
            // ChannelState's own default (never-yet-encoded) Delta is 0 -- the very first block of a
            // stream must still seed a valid, nonzero divisor before CompressSample's own
            // division-by-Delta runs, matching FFmpeg's own real
            // "if (c->status[i].idelta < 16) c->status[i].idelta = 16;" seeding step.
            var states = new MsAdpcmDecoder.ChannelState[1]; // Delta defaults to 0
            var samples = new[] { 100, 110, 120, 130, 140, 150, 160, 170, 180, 190 };
            var block = new byte[7 + 4];

            var act = () => MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 10, states, block);

            act.Should().NotThrow();
            var writtenDelta = (short)(block[1] | (block[2] << 8));
            writtenDelta.Should().Be(16);
        }

        [Fact]
        public void EncodeBlock_HeaderDelta_Should_Saturate_Not_Wrap_When_It_Exceeds_Int16Range()
        {
            // Delta can realistically exceed int16 range after a run of repeated maximum-magnitude
            // nibbles (see this file's own top-of-file doc comment for why, and the computed example
            // there) -- confirms the header's own delta field saturates to short.MaxValue instead of
            // wrapping via a raw (short) cast (which would silently produce a negative, WRONG value,
            // not an exception -- so a "doesn't throw" check alone can't catch this; the actual
            // decoded value has to be checked, the same lesson IMA ADPCM's own analogous header-clamp
            // test already applied). samplesPerBlock is deliberately the minimum (2, header only, no
            // nibble-encoding loop afterward) so state.Delta's own post-call value reflects exactly
            // this clamp and nothing else -- with any real data loop, normal adaptation would change
            // Delta again afterward, which is a separate, legitimate behavior this test isn't about.
            var states = new[] { new MsAdpcmDecoder.ChannelState { Sample1 = 0, Sample2 = 0, Delta = int.MaxValue / 2 } };
            var samples = new[] { 100, 110 };
            var block = new byte[7];

            MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 2, states, block);

            var writtenDelta = (short)(block[1] | (block[2] << 8));
            writtenDelta.Should().Be(short.MaxValue);
            states[0].Delta.Should().Be(short.MaxValue, "the running ChannelState itself must be clamped too, not just the bytes written, or this encoder's own internal state would diverge from what a decoder reconstructs from the header it actually reads");
        }

        [Theory]
        [InlineData(int.MinValue, short.MinValue)]
        [InlineData(int.MaxValue, short.MaxValue)]
        public void EncodeBlock_WithOutOfContractExtremeFirstSample_Should_Saturate_Not_Wrap(int extremeFirstSample, short expectedSaturated)
        {
            // Mirrors ImaAdpcmEncoderTest's own analogous test: the block's own header predictor
            // samples -- the block's first two raw samples, written directly without going through
            // CompressSample's own separate clamp -- must saturate to the native 16-bit range instead
            // of wrapping via a raw (short) cast.
            var samples = new int[10];
            samples[0] = extremeFirstSample;
            var encodeStates = new MsAdpcmDecoder.ChannelState[1];
            var block = new byte[7 + 4];

            MsAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 10, encodeStates, block);

            var decodeStates = new MsAdpcmDecoder.ChannelState[1];
            var decoded = new int[10];
            MsAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 10, decodeStates, MsAdpcmEncoder.Coeff1Table, MsAdpcmEncoder.Coeff2Table, decoded);

            decoded[0].Should().Be(expectedSaturated);
        }

        [Fact]
        public void Coeff1Table_And_Coeff2Table_Should_Have_The_Same_Length()
        {
            MsAdpcmEncoder.Coeff1Table.Length.Should().Be(MsAdpcmEncoder.Coeff2Table.Length);
        }

        [Fact]
        public void Coeff1Table_FirstEntry_Should_Be_The_SimplePredictor_Pair_This_Encoder_Always_Uses()
        {
            MsAdpcmEncoder.Coeff1Table[0].Should().Be(256);
            MsAdpcmEncoder.Coeff2Table[0].Should().Be(0);
        }
    }
}
