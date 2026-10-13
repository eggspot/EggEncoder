using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class ImaAdpcmEncoderTest
    {
        [Fact]
        public void EncodeBlock_Mono_Should_Produce_A_Block_DecodeBlock_Can_Read_Back_Verbatim_First_Sample()
        {
            const int samplesPerBlock = 9;
            var samples = new[] { 1234, 1250, 1300, 1280, 1260, 1240, 1220, 1200, 1190 };
            var encodeStates = new ImaAdpcmDecoder.ChannelState[1];
            var block = new byte[4 + ((samplesPerBlock - 1) / 2)];

            ImaAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock, encodeStates, block);

            var decodeStates = new ImaAdpcmDecoder.ChannelState[1];
            var decoded = new int[samplesPerBlock];
            ImaAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock, decodeStates, decoded);

            decoded[0].Should().Be(1234, "the block's own first sample is always written verbatim into the header, never nibble-quantized");
            for (var i = 1; i < samplesPerBlock; i++)
            {
                Math.Abs(decoded[i] - samples[i]).Should().BeLessThan(50, $"sample {i} should reconstruct within a reasonable quantization tolerance");
            }
        }

        [Fact]
        public void EncodeBlock_Stereo_Should_Keep_Channels_Independent()
        {
            // Two genuinely different per-channel signals -- confirms EncodeBlock's own per-channel
            // header layout and ChannelState indexing don't cross-contaminate (channel 0's header/data
            // written to channel 1's slot or vice versa).
            const int samplesPerBlock = 9;
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
                1080, 4840
            };
            var encodeStates = new ImaAdpcmDecoder.ChannelState[2];
            var block = new byte[8 + ((samplesPerBlock - 1) / 2 * 2)];

            ImaAdpcmEncoder.EncodeBlock(samples, channels: 2, samplesPerBlock, encodeStates, block);

            var decodeStates = new ImaAdpcmDecoder.ChannelState[2];
            var decoded = new int[samplesPerBlock * 2];
            ImaAdpcmDecoder.DecodeBlock(block, channels: 2, samplesPerBlock, decodeStates, decoded);

            decoded[0].Should().Be(1000, "frame 0, channel 0 is its own raw header predictor");
            decoded[1].Should().Be(5000, "frame 0, channel 1 is its own raw header predictor");
            for (var frame = 1; frame < samplesPerBlock; frame++)
            {
                Math.Abs(decoded[(frame * 2) + 0] - samples[(frame * 2) + 0]).Should().BeLessThan(50, $"frame {frame}, channel 0");
                Math.Abs(decoded[(frame * 2) + 1] - samples[(frame * 2) + 1]).Should().BeLessThan(50, $"frame {frame}, channel 1");
            }
        }

        [Fact]
        public void EncodeBlock_HeaderStepIndex_Should_Reflect_The_CallerSupplied_Starting_Value_Not_This_Blocks_Own_Ending_One()
        {
            // The predictor always resets to each block's own raw first sample (covered by the round-
            // trip tests above), but the header's own step-index BYTE is written before any of this
            // call's own encoding happens (confirmed by reading EncodeBlock's own source, not assumed)
            // -- it reflects whatever step index carried over from the END of the PREVIOUS block (the
            // caller-supplied starting state), the same way FFmpeg's own real adpcmenc.c writes
            // status->step_index to the header before encoding that block's own samples. A decoder uses
            // the header purely as its own starting point for THIS block, so this is correct, not a
            // bug -- confirmed by first writing this test with the opposite (wrong) assumption, which
            // failed, revealing the actual (correct) behavior.
            var states = new[] { new ImaAdpcmDecoder.ChannelState { Predictor = 999, StepIndex = 42 } };
            var samples = new[] { 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070, 1080 };
            var block = new byte[4 + 4];

            ImaAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 9, states, block);

            block[2].Should().Be(42, "the header's own step-index byte must reflect the state carried in from the end of the previous block, not whatever this block's own encoding ends up leaving it at");
        }

        [Theory]
        [InlineData(int.MinValue, short.MinValue)]
        [InlineData(int.MaxValue, short.MaxValue)]
        public void EncodeBlock_WithOutOfContractExtremeFirstSample_Should_Saturate_Not_Wrap(int extremeFirstSample, short expectedSaturated)
        {
            // This format's own documented contract is 16-bit input, but nothing stops a caller from
            // passing an out-of-range int anyway. Confirms the block's own header predictor -- the
            // block's first raw sample, written directly without going through QuantizeNibble's own
            // separate clamp -- saturates to the native 16-bit range instead of wrapping via a raw
            // (short) cast (e.g. a naive (short)int.MinValue truncates to 0, a silently WRONG value,
            // not an exception -- so a "doesn't throw" check alone can't catch this; the actual decoded
            // value must be checked). Confirmed this gap was real by temporarily reverting just this
            // clamp and rerunning the existing WavWriter-level regression test: it still passed, since
            // truncation never throws -- only a direct, value-checking test like this one catches it.
            var samples = new int[9];
            samples[0] = extremeFirstSample;
            var encodeStates = new ImaAdpcmDecoder.ChannelState[1];
            var block = new byte[4 + 4];

            ImaAdpcmEncoder.EncodeBlock(samples, channels: 1, samplesPerBlock: 9, encodeStates, block);

            var decodeStates = new ImaAdpcmDecoder.ChannelState[1];
            var decoded = new int[9];
            ImaAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 9, decodeStates, decoded);

            decoded[0].Should().Be(expectedSaturated);
        }
    }
}
