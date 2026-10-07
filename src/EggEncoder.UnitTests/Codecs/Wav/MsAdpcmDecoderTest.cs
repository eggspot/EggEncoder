using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class MsAdpcmDecoderTest
    {
        [Fact]
        public void ExpandNibble_WithPositiveNibble_Should_Increase_Predictor()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 100, Sample2 = 50, Coeff1 = 256, Coeff2 = 0, Delta = 16 };

            // predictor = (100*256 + 50*0)/256 = 100; nibble 5 (sign 0) -> +5*16 = 180.
            var sample = MsAdpcmDecoder.ExpandNibble(ref state, nibble: 5);

            sample.Should().Be(180);
            state.Sample1.Should().Be(180);
            state.Sample2.Should().Be(100, "sample1 shifts into sample2 every call");
        }

        [Fact]
        public void ExpandNibble_WithSignBitSet_Should_Decrease_Predictor()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 100, Sample2 = 50, Coeff1 = 256, Coeff2 = 0, Delta = 16 };

            // nibble 13 (0b1101) = sign bit set, signed value 13-16=-3 -> predictor 100 + (-3*16) = 52.
            var sample = MsAdpcmDecoder.ExpandNibble(ref state, nibble: 13);

            sample.Should().Be(52);
        }

        [Fact]
        public void ExpandNibble_UsesBothCoefficients_Not_Just_Coeff1()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 100, Sample2 = 50, Coeff1 = 192, Coeff2 = 64, Delta = 16 };

            // predictor = (100*192 + 50*64)/256 = 22400/256 = 87 (integer truncation); nibble 0 adds nothing.
            var sample = MsAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            sample.Should().Be(87, "a nonzero Coeff2 must actually contribute -- Coeff1 alone would give 75");
        }

        [Fact]
        public void ExpandNibble_PredictorOverflow_Should_Clamp_To_Int16Range()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 32000, Sample2 = 0, Coeff1 = 256, Coeff2 = 0, Delta = 1000 };

            var sample = MsAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            sample.Should().Be(short.MaxValue);
            state.Sample1.Should().Be(short.MaxValue);
        }

        [Fact]
        public void ExpandNibble_PredictorUnderflow_Should_Clamp_To_Int16Range()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = -32000, Sample2 = 0, Coeff1 = 256, Coeff2 = 0, Delta = 1000 };

            var sample = MsAdpcmDecoder.ExpandNibble(ref state, nibble: 15);

            sample.Should().Be(short.MinValue);
            state.Sample1.Should().Be(short.MinValue);
        }

        [Fact]
        public void ExpandNibble_DeltaAdaptation_Should_Never_Go_Below_Sixteen()
        {
            // AdaptationTable[0] = 230; (230*16)>>8 = 14 (truncated), which is below the floor.
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 0, Sample2 = 0, Coeff1 = 0, Coeff2 = 0, Delta = 16 };

            MsAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            state.Delta.Should().Be(16, "the adaptation table would otherwise shrink delta below its documented floor");
        }

        [Fact]
        public void ExpandNibble_DeltaAdaptation_Should_Grow_For_A_LargeMagnitude_Nibble()
        {
            // AdaptationTable[4] (and [8], the sign-bit-set mirror) = 307, > 256, so delta grows.
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 0, Sample2 = 0, Coeff1 = 0, Coeff2 = 0, Delta = 100 };

            MsAdpcmDecoder.ExpandNibble(ref state, nibble: 4);

            state.Delta.Should().Be(119, "(307*100)>>8 = 119, truncated");
        }

        [Fact]
        public void DecodeBlock_Mono_FirstTwoFrames_Should_Be_The_Raw_Header_Sample2_Then_Sample1()
        {
            // Header: predictor=0 (coeff1=256,coeff2=0, via a 1-entry table), delta=16, sample1=100,
            // sample2=50 -- 7 bytes total for mono. One data byte (2 nibbles) follows, giving
            // samplesPerBlock=4 (2 header frames + 2 nibble-decoded).
            byte[] block =
            [
                0, // block predictor (index 0)
                16, 0, // delta = 16
                100, 0, // sample1 = 100
                50, 0, // sample2 = 50
                0x50 // one data byte: nibbles 5 and 0
            ];
            short[] coeff1 = [256];
            short[] coeff2 = [0];
            var states = new MsAdpcmDecoder.ChannelState[1];
            var output = new int[4];

            MsAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 4, states, coeff1, coeff2, output);

            output[0].Should().Be(50, "frame 0 is the header's own sample2, output before sample1");
            output[1].Should().Be(100, "frame 1 is the header's own sample1");
            output[2].Should().Be(180, "nibble 5: predictor (100*256)/256=100, +5*16=180");
            output[3].Should().Be(180, "nibble 0: predictor (180*256+100*0)/256=180, +0*16=180 (coincidentally the same value as frame 2)");
        }

        [Fact]
        public void DecodeBlock_Stereo_Should_Interleave_OneNibblePerChannel_PerByte()
        {
            // Two 7-byte-equivalent header fields, grouped by field (not by channel) as the real
            // format requires: both predictors, then both deltas, then both sample1s, then both
            // sample2s. One data byte follows: high nibble -> channel 0, low nibble -> channel 1.
            byte[] block =
            [
                0, 0, // block predictors: ch0=0, ch1=0 (both index into the same 1-entry table)
                16, 0, 16, 0, // deltas: ch0=16, ch1=16
                100, 0, 1000 & 0xFF, (1000 >> 8) & 0xFF, // sample1: ch0=100, ch1=1000
                50, 0, 500 & 0xFF, (500 >> 8) & 0xFF, // sample2: ch0=50, ch1=500
                0x53 // nibble 5 -> ch0, nibble 3 -> ch1
            ];
            short[] coeff1 = [256];
            short[] coeff2 = [0];
            var states = new MsAdpcmDecoder.ChannelState[2];
            var output = new int[2 * 3];

            MsAdpcmDecoder.DecodeBlock(block, channels: 2, samplesPerBlock: 3, states, coeff1, coeff2, output);

            output[0].Should().Be(50, "frame 0, channel 0 is ch0's header sample2");
            output[1].Should().Be(500, "frame 0, channel 1 is ch1's header sample2");
            output[2].Should().Be(100, "frame 1, channel 0 is ch0's header sample1");
            output[3].Should().Be(1000, "frame 1, channel 1 is ch1's header sample1");
            output[4].Should().Be(180, "frame 2, channel 0: nibble 5 -> (100*256)/256 + 5*16 = 180");
            output[5].Should().Be(1048, "frame 2, channel 1: nibble 3 -> (1000*256)/256 + 3*16 = 1048");
        }

        [Fact]
        public void DecodeBlock_Mono_WithOddTrailingSampleCount_Should_Waste_The_Final_Nibble()
        {
            // samplesPerBlock=5 -> remainingSamplesPerChannel=3 (odd), the same "wasted trailing
            // nibble" branch IMA ADPCM has -- the data loop must decode exactly 3 more samples from
            // 2 data bytes. Each byte's upper nibble is consumed first, then its lower nibble only if
            // there's room -- so for the final byte here, the upper nibble (0xF) IS decoded and the
            // lower nibble (0x7) is the one that must never be.
            byte[] block =
            [
                0, 16, 0, 0, 0, 0, 0, // header: predictor=0, delta=16, sample1=0, sample2=0
                0x00, 0xF7 // two data bytes; second byte's lower nibble (0x7) must never be decoded
            ];
            short[] coeff1 = [256];
            short[] coeff2 = [0];
            var states = new MsAdpcmDecoder.ChannelState[1];
            var output = new int[5];

            MsAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 5, states, coeff1, coeff2, output);

            // Ground truth via the same nibble sequence (0, 0, 15) driven independently through
            // ExpandNibble -- if the wasted lower nibble (0x7) were decoded instead, this wouldn't
            // match (and a true overrun would have already thrown above).
            var expectedState = new MsAdpcmDecoder.ChannelState { Coeff1 = 256, Coeff2 = 0, Delta = 16 };
            var sample1 = MsAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 0);
            var sample2 = MsAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 0);
            var sample3 = MsAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 15);

            output[2].Should().Be(sample1);
            output[3].Should().Be(sample2);
            output[4].Should().Be(sample3);
            states[0].Sample1.Should().Be(expectedState.Sample1, "the wasted lower nibble (0x7) must never be decoded");
        }

        [Fact]
        public void DecodeBlock_WithBlockPredictorBeyondTableRange_Should_Clamp_Not_Throw()
        {
            // The block predictor is read from untrusted per-block data, not a structural header
            // field -- a corrupted byte of 200 against a 1-entry table must clamp to index 0, not
            // throw IndexOutOfRangeException.
            byte[] block = [200, 16, 0, 0, 0, 0, 0];
            short[] coeff1 = [256];
            short[] coeff2 = [0];
            var states = new MsAdpcmDecoder.ChannelState[1];
            var output = new int[2];

            var act = () => MsAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 2, states, coeff1, coeff2, output);

            act.Should().NotThrow();
            states[0].Coeff1.Should().Be(256, "clamped to the only valid index, 0");
        }

        [Fact]
        public void CompressSample_Should_Commit_A_Nibble_That_ExpandNibble_Reconstructs_Close_To_Target()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 100, Sample2 = 50, Coeff1 = 256, Coeff2 = 0, Delta = 16 };

            var nibble = MsAdpcmDecoder.CompressSample(ref state, sample: 180);

            nibble.Should().BeInRange(0, 15);
            Math.Abs(state.Sample1 - 180).Should().BeLessThan(16, "the committed nibble's reconstruction should land within one delta step of the target");
        }

        [Fact]
        public void CompressSample_WithNegativeDelta_Should_Set_The_SignBit()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 1000, Sample2 = 1000, Coeff1 = 256, Coeff2 = 0, Delta = 16 };

            var nibble = MsAdpcmDecoder.CompressSample(ref state, sample: 0);

            (nibble & 8).Should().Be(8, "the target sample is below the predictor, so the sign bit must be set");
            state.Sample1.Should().BeLessThan(1000);
        }

        [Fact]
        public void CompressSample_WithPositiveDelta_Should_Clear_The_SignBit()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 0, Sample2 = 0, Coeff1 = 256, Coeff2 = 0, Delta = 16 };

            var nibble = MsAdpcmDecoder.CompressSample(ref state, sample: 1000);

            (nibble & 8).Should().Be(0, "the target sample is above the predictor, so the sign bit must be clear");
            state.Sample1.Should().BeGreaterThan(0);
        }

        [Fact]
        public void CompressSample_CalledRepeatedly_Should_Track_A_Slowly_Varying_Signal_Closely()
        {
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = 0, Sample2 = 0, Coeff1 = 256, Coeff2 = 0, Delta = 16 };
            var maxAbsoluteError = 0;

            for (var i = 0; i < 200; i++)
            {
                var target = (int)(8000 * Math.Sin(i * 0.05));
                MsAdpcmDecoder.CompressSample(ref state, target);
                maxAbsoluteError = Math.Max(maxAbsoluteError, Math.Abs(target - state.Sample1));
            }

            maxAbsoluteError.Should().BeLessThan(2000, "a smooth, slowly-varying signal should stay well within the quantizer's adaptive delta range once it has ramped up");
        }

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void CompressSample_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw(int extremeSample)
        {
            // This format's own documented contract is 16-bit input, but nothing stops a caller from
            // passing an out-of-range int anyway. Without clamping, a sample of int.MinValue or
            // int.MaxValue combined with a predictor near the opposite extreme could overflow the
            // (diff + bias) addition or the nibble clamp's own arithmetic -- the same class of bug
            // IMA ADPCM's own QuantizeNibble needed fixing for (see its own doc comment), checked for
            // proactively here rather than waiting for a real crash to reveal it.
            var state = new MsAdpcmDecoder.ChannelState { Sample1 = short.MinValue, Sample2 = short.MaxValue, Coeff1 = 460, Coeff2 = -208, Delta = 16 };

            var act = () => MsAdpcmDecoder.CompressSample(ref state, extremeSample);

            act.Should().NotThrow();
        }
    }
}
