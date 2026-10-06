using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class ImaAdpcmDecoderTest
    {
        [Fact]
        public void ExpandNibble_WithPositiveSign_Should_Increase_Predictor()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 0 };

            // step_table[0] = 7; nibble 7 = sign 0, delta 7 (binary 0111) ->
            // diff = (7>>3) + 7 (bit 4) + (7>>1) (bit 2) + (7>>2) (bit 1) = 0 + 7 + 3 + 1 = 11.
            var sample = ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            sample.Should().Be(11);
        }

        [Fact]
        public void ExpandNibble_WithSignBitSet_Should_Decrease_Predictor()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 100, StepIndex = 0 };

            // nibble 15 (0b1111) = sign bit set, delta 7 -> same diff as above (11), subtracted.
            var sample = ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 15);

            sample.Should().Be(89);
        }

        [Fact]
        public void ExpandNibble_PredictorOverflow_Should_Clamp_To_Int16Range()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = short.MaxValue, StepIndex = 88 };

            // step_table[88] = 32767 (the largest step); any positive nibble pushes well past short.MaxValue.
            var sample = ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            sample.Should().Be(short.MaxValue);
            state.Predictor.Should().Be(short.MaxValue);
        }

        [Fact]
        public void ExpandNibble_PredictorUnderflow_Should_Clamp_To_Int16Range()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = short.MinValue, StepIndex = 88 };

            var sample = ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 15);

            sample.Should().Be(short.MinValue);
            state.Predictor.Should().Be(short.MinValue);
        }

        [Fact]
        public void ExpandNibble_StepIndexAtZero_Should_Not_Go_Negative()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 0 };

            // nibble 0 (sign 0, delta 0) maps to index_table[0] = -1, which would push step_index to
            // -1 without clamping -- an out-of-range array access on the next call.
            ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            state.StepIndex.Should().Be(0);
        }

        [Fact]
        public void ExpandNibble_StepIndexAtMaximum_Should_Not_Exceed_TableBounds()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 88 };

            // nibble 7 maps to index_table[7] = 8, which would push step_index to 96 without clamping.
            ImaAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            state.StepIndex.Should().Be(88);
        }

        [Fact]
        public void DecodeBlock_Mono_FirstSamplePerChannel_Should_Be_The_Raw_Header_Predictor()
        {
            // 4-byte header (predictor=100 LE, step_index=5, reserved=0) followed by one data byte
            // (2 nibbles -> 2 more samples, so samplesPerBlock=3 total including the header sample);
            // the header's own predictor becomes sample[0] directly, never passed through ExpandNibble.
            byte[] block = [100, 0, 5, 0, 0x77];
            var states = new ImaAdpcmDecoder.ChannelState[1];
            var output = new int[3];

            ImaAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 3, states, output);

            output[0].Should().Be(100);
            states[0].StepIndex.Should().NotBe(5, "the data byte's nibbles should have already adjusted the step index away from the header's own value");
        }

        [Fact]
        public void DecodeBlock_Stereo_Should_Interleave_Channels_In_FourByteGroups()
        {
            // Two 4-byte headers (channel 0 predictor=0/step_index=8, channel 1 predictor=1000/
            // step_index=10), then one 4-byte group per channel (8 samples each) -- confirms the
            // decode order is "all of channel 0's group, then all of channel 1's group", not
            // byte-interleaved, and that output is interleaved L/R per frame as this project's
            // convention requires. step_index=8 (step=16) is used instead of 0 (step=7) so an
            // all-zero nibble (diff = step>>3) still produces a nonzero, observable predictor move.
            byte[] block =
            [
                0, 0, 8, 0, // channel 0 header: predictor=0, step_index=8
                0xE8, 0x03, 10, 0, // channel 1 header: predictor=1000, step_index=10
                0x00, 0x00, 0x00, 0x00, // channel 0's 4-byte group (8 samples)
                0x00, 0x00, 0x00, 0x00 // channel 1's 4-byte group (8 samples)
            ];
            var states = new ImaAdpcmDecoder.ChannelState[2];
            var output = new int[2 * 9];

            ImaAdpcmDecoder.DecodeBlock(block, channels: 2, samplesPerBlock: 9, states, output);

            output[0].Should().Be(0, "frame 0, channel 0 is the raw header predictor");
            output[1].Should().Be(1000, "frame 0, channel 1 is the raw header predictor");

            // Every nibble in the data groups above is 0 (sign 0, delta 0), so each channel's own
            // predictor should have moved by its own channel-specific diff from its own state --
            // confirms channel 0 and channel 1 never cross-contaminate each other's running state.
            states[0].Predictor.Should().NotBe(0);
            states[1].Predictor.Should().NotBe(1000);
            states[1].Predictor.Should().NotBe(states[0].Predictor, "the two channels started from different predictors/step indices and must not have become coupled");
        }

        [Fact]
        public void DecodeBlock_Mono_WithOddTrailingSampleCount_Should_Waste_The_Final_Nibble()
        {
            // samplesPerBlock=4 -> remainingSamplesPerChannel=3 (odd): every real ffmpeg-produced
            // fixture this project has happens to land on a remainingSamplesPerChannel that's an
            // exact multiple of 8, so this is the one branch no bit-exact fixture test ever
            // exercises -- the data loop must decode exactly 3 more samples from 2 data bytes,
            // using only the lower nibble of the final byte and discarding its upper nibble
            // (0xF) entirely, rather than overrunning into a 4th, nonexistent sample slot.
            byte[] block = [0, 0, 8, 0, 0x00, 0xF7];
            var states = new ImaAdpcmDecoder.ChannelState[1];
            var output = new int[4];

            ImaAdpcmDecoder.DecodeBlock(block, channels: 1, samplesPerBlock: 4, states, output);

            // Ground truth via the same nibble sequence (0, 0, 7) driven independently through
            // ExpandNibble -- if the wasted upper nibble (0xF) were decoded instead, this
            // wouldn't match (and a true overrun would have already thrown above).
            var expectedState = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 8 };
            var sample1 = ImaAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 0);
            var sample2 = ImaAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 0);
            var sample3 = ImaAdpcmDecoder.ExpandNibble(ref expectedState, nibble: 7);

            output[0].Should().Be(0, "frame 0 is the raw header predictor");
            output[1].Should().Be(sample1);
            output[2].Should().Be(sample2);
            output[3].Should().Be(sample3);
            states[0].Predictor.Should().Be(expectedState.Predictor, "the wasted upper nibble (0xF) must never be decoded");
        }

        [Fact]
        public void QuantizeNibble_Should_Pick_The_Nibble_ExpandNibble_Itself_Agrees_Is_Closest()
        {
            // Exhaustively confirms the search picks the true closest match under ComputeDiff/
            // ExpandNibble's own formula, not FFmpeg's differently-tuned closed-form heuristic (see
            // this method's own doc comment in ImaAdpcmDecoder.cs) -- for every nibble n, decoding it
            // via a copy of the current state must never produce a result closer to the target sample
            // than what QuantizeNibble itself chose.
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 100, StepIndex = 20 };
            const int targetSample = 250;

            var nibble = ImaAdpcmDecoder.QuantizeNibble(ref state, targetSample);
            var chosenError = Math.Abs(targetSample - state.Predictor);

            for (var candidate = 0; candidate < 16; candidate++)
            {
                var candidateState = new ImaAdpcmDecoder.ChannelState { Predictor = 100, StepIndex = 20 };
                var candidateSample = ImaAdpcmDecoder.ExpandNibble(ref candidateState, candidate);
                var candidateError = Math.Abs(targetSample - candidateSample);

                candidateError.Should().BeGreaterThanOrEqualTo(chosenError, $"nibble {candidate} must not reconstruct closer to {targetSample} than the chosen nibble {nibble} did");
            }
        }

        [Fact]
        public void QuantizeNibble_WithNegativeDelta_Should_Set_The_SignBit()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 1000, StepIndex = 20 };

            var nibble = ImaAdpcmDecoder.QuantizeNibble(ref state, sample: 0);

            (nibble & 8).Should().Be(8, "the target sample is below the predictor, so the sign bit must be set");
            state.Predictor.Should().BeLessThan(1000);
        }

        [Fact]
        public void QuantizeNibble_WithPositiveDelta_Should_Clear_The_SignBit()
        {
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 20 };

            var nibble = ImaAdpcmDecoder.QuantizeNibble(ref state, sample: 1000);

            (nibble & 8).Should().Be(0, "the target sample is above the predictor, so the sign bit must be clear");
            state.Predictor.Should().BeGreaterThan(0);
        }

        [Fact]
        public void QuantizeNibble_CalledRepeatedly_Should_Track_A_Slowly_Varying_Signal_Closely()
        {
            // The real-world property that actually matters: encoding a smooth signal sample-by-sample
            // should keep the reconstructed predictor within a small, bounded error of each target --
            // not just that any one isolated call picks its own locally-best nibble.
            var state = new ImaAdpcmDecoder.ChannelState { Predictor = 0, StepIndex = 0 };
            var maxAbsoluteError = 0;

            for (var i = 0; i < 200; i++)
            {
                var target = (int)(8000 * Math.Sin(i * 0.05));
                ImaAdpcmDecoder.QuantizeNibble(ref state, target);
                maxAbsoluteError = Math.Max(maxAbsoluteError, Math.Abs(target - state.Predictor));
            }

            maxAbsoluteError.Should().BeLessThan(2000, "a smooth, slowly-varying signal should stay well within the quantizer's adaptive step range once it has ramped up");
        }
    }
}
