using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class YamahaAdpcmDecoderTest
    {
        [Fact]
        public void ExpandNibble_WithStepAtZero_Should_LazyInit_PredictorAndStep_RegardlessOfExistingPredictor()
        {
            // Confirmed from FFmpeg's real adpcm_yamaha_expand_nibble: initialization is keyed off
            // state.Step == 0 (the never-yet-decoded default), not a separate "first call" flag -- and
            // it resets BOTH predictor and step, discarding whatever Predictor already held. If
            // lazy-init didn't fire, this call would start from Predictor=999 instead of 0, producing
            // a very different (and wrong) result.
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 999, Step = 0 };

            // predictor = 0 (lazy-init) + (127 * DiffLookup[0]=1) / 8 = 15; step clamps up to 127.
            var sample = YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            sample.Should().Be(15);
            state.Step.Should().Be(127);
        }

        [Fact]
        public void ExpandNibble_WithPositiveSign_Should_Increase_Predictor()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 100, Step = 200 };

            // nibble 0 -> DiffLookup[0] = 1 (positive) -> predictor += (200 * 1) / 8 = 25
            var sample = YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            sample.Should().Be(125);
        }

        [Fact]
        public void ExpandNibble_WithSignBitSet_Should_Decrease_Predictor()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 100, Step = 200 };

            // nibble 8 -> DiffLookup[8] = -1 -> predictor += (200 * -1) / 8 = -25
            var sample = YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 8);

            sample.Should().Be(75);
        }

        [Fact]
        public void ExpandNibble_PredictorOverflow_Should_Clamp_To_Int16Range()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = short.MaxValue, Step = 24576 };

            // nibble 7 -> DiffLookup[7] = 15, the largest positive entry; any step pushes well past short.MaxValue.
            var sample = YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            sample.Should().Be(short.MaxValue);
            state.Predictor.Should().Be(short.MaxValue);
        }

        [Fact]
        public void ExpandNibble_PredictorUnderflow_Should_Clamp_To_Int16Range()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = short.MinValue, Step = 24576 };

            // nibble 15 -> DiffLookup[15] = -15, the largest negative entry.
            var sample = YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 15);

            sample.Should().Be(short.MinValue);
            state.Predictor.Should().Be(short.MinValue);
        }

        [Fact]
        public void ExpandNibble_StepAtMinimum_Should_Not_Go_Below_The_Floor_Of_127()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 127 };

            // nibble 0 -> IndexScale[0] = 230; (127 * 230) >> 8 = 114, below the 127 floor.
            YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 0);

            state.Step.Should().Be(127);
        }

        [Fact]
        public void ExpandNibble_StepAtMaximum_Should_Not_Exceed_The_Ceiling_Of_24576()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 24576 };

            // nibble 7 -> IndexScale[7] = 614 (the largest entry); (24576 * 614) >> 8 = 58944, above the 24576 ceiling.
            YamahaAdpcmDecoder.ExpandNibble(ref state, nibble: 7);

            state.Step.Should().Be(24576);
        }

        [Theory]
        [InlineData(0, 230)]
        [InlineData(4, 307)]
        [InlineData(6, 512)]
        [InlineData(7, 614)]
        [InlineData(8, 230)]
        [InlineData(15, 614)]
        public void ExpandNibble_IndexScale_Should_Be_Symmetric_Between_Positive_And_Negative_DiffLookup_Pairs(int nibble, int expectedScaleNumerator)
        {
            // ff_adpcm_yamaha_indexscale is symmetric (the first 8 entries repeat for the last 8)
            // since the step-size scale depends only on the difference's magnitude, not its sign --
            // confirmed verbatim from FFmpeg's real adpcm_data.c. A step of exactly 256 makes the
            // scale's own numerator directly observable in the resulting step (256 * scale >> 8 ==
            // scale, when scale alone is already below the 24576 ceiling).
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 256 };

            YamahaAdpcmDecoder.ExpandNibble(ref state, nibble);

            state.Step.Should().Be(expectedScaleNumerator);
        }
    }
}
