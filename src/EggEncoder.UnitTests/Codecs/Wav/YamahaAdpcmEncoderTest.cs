using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class YamahaAdpcmEncoderTest
    {
        [Fact]
        public void CompressSample_WithStepAtZero_Should_LazyInit_Before_Computing_Delta()
        {
            // Confirmed from FFmpeg's real adpcm_yamaha_compress_sample: this lazy-init check is
            // duplicated here rather than relying on ExpandNibble's own copy of it, because the
            // nibble computation itself divides by state.Step before ExpandNibble ever runs. If
            // init didn't fire before that division, dividing by the still-zero Step would throw
            // DivideByZeroException -- the strongest possible confirmation that this really happens
            // before the nibble computation, not just before ExpandNibble's own commit step.
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 999, Step = 0 };

            var act = () => YamahaAdpcmEncoder.CompressSample(ref state, sample: 0);

            act.Should().NotThrow();
        }

        [Fact]
        public void CompressSample_WithPositiveDelta_Should_Clear_The_SignBit()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 200 };

            // delta = 1000 - 0 = 1000; min(7, abs(1000) * 4 / 200) = min(7, 20) = 7; delta >= 0 -> sign bit clear.
            var nibble = YamahaAdpcmEncoder.CompressSample(ref state, sample: 1000);

            nibble.Should().Be(7);
            (nibble & 8).Should().Be(0, "the target sample is above the predictor, so the sign bit must be clear");
        }

        [Fact]
        public void CompressSample_WithNegativeDelta_Should_Set_The_SignBit()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 1000, Step = 200 };

            // delta = 0 - 1000 = -1000; min(7, abs(-1000) * 4 / 200) = min(7, 20) = 7; delta < 0 -> sign bit set -> nibble = 7 | 8 = 15.
            var nibble = YamahaAdpcmEncoder.CompressSample(ref state, sample: 0);

            nibble.Should().Be(15);
            (nibble & 8).Should().Be(8, "the target sample is below the predictor, so the sign bit must be set");
        }

        [Fact]
        public void CompressSample_WithSmallDelta_Should_Pick_A_Proportionally_Small_Nibble()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 200 };

            // delta = 50; min(7, abs(50) * 4 / 200) = min(7, 1) = 1.
            var nibble = YamahaAdpcmEncoder.CompressSample(ref state, sample: 50);

            nibble.Should().Be(1);
        }

        [Fact]
        public void CompressSample_WithLargeDelta_Should_Saturate_At_SevenRatherThan_Exceed_The_FourBitMagnitudeRange()
        {
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 1 };

            // delta = 1000; abs(1000) * 4 / 1 = 4000, far beyond the 3-bit (0..7) magnitude range the
            // sign bit leaves available -- must saturate at 7, not wrap or overflow into the sign bit.
            var nibble = YamahaAdpcmEncoder.CompressSample(ref state, sample: 1000);

            nibble.Should().Be(7);
        }

        [Fact]
        public void CompressSample_Should_Commit_State_Via_The_Same_ExpandNibble_Formula_Decode_Uses()
        {
            // Unlike IMA ADPCM's own QuantizeNibble (an 8-candidate search, because FFmpeg's real IMA
            // encoder's heuristic genuinely disagrees with its own decoder's state formula -- see
            // ImaAdpcmDecoder's own doc comment), Yamaha ADPCM's real encoder computes the nibble via
            // a direct closed-form formula and then commits it through the EXACT SAME state-update
            // ExpandNibble decodes with (confirmed directly from FFmpeg's adpcmenc.c source) -- so an
            // encoder's own running state must track a decoder fed the same nibble in lockstep, by
            // construction, not by coincidence.
            var encodeState = new YamahaAdpcmDecoder.ChannelState { Predictor = 100, Step = 300 };
            var decodeState = encodeState;

            var nibble = YamahaAdpcmEncoder.CompressSample(ref encodeState, sample: 250);
            var decodedSample = YamahaAdpcmDecoder.ExpandNibble(ref decodeState, nibble);

            encodeState.Predictor.Should().Be(decodeState.Predictor);
            encodeState.Step.Should().Be(decodeState.Step);
            decodedSample.Should().Be(decodeState.Predictor);
        }

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void CompressSample_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw(int extremeSample)
        {
            // This format's own documented contract is 16-bit input, but nothing stops a caller from
            // passing an out-of-range int anyway (WavWriter is a public type, reachable without going
            // through AudioCutter's own pipeline). Without the defensive clamp at this method's own
            // entry point, an extreme delta could in principle overflow computing abs(delta) * 4 --
            // the same "clamp, don't crash, at an encode entry point" precedent
            // ImaAdpcmDecoder.QuantizeNibble/MsAdpcmDecoder.CompressSample/G711Codec.ClampToTableIndex
            // already establish.
            var state = new YamahaAdpcmDecoder.ChannelState { Predictor = 0, Step = 200 };

            var act = () => YamahaAdpcmEncoder.CompressSample(ref state, extremeSample);

            act.Should().NotThrow();
        }

        [Fact]
        public void CompressSample_CalledRepeatedly_Should_Track_A_Slowly_Varying_Signal_Closely()
        {
            // The real-world property that actually matters: encoding a smooth signal sample-by-sample
            // should keep the reconstructed predictor within a small, bounded error of each target --
            // not just that any one isolated call picks its own locally-reasonable nibble.
            var state = new YamahaAdpcmDecoder.ChannelState();
            var maxAbsoluteError = 0;

            for (var i = 0; i < 200; i++)
            {
                var target = (int)(8000 * Math.Sin(i * 0.05));
                YamahaAdpcmEncoder.CompressSample(ref state, target);
                maxAbsoluteError = Math.Max(maxAbsoluteError, Math.Abs(target - state.Predictor));
            }

            maxAbsoluteError.Should().BeLessThan(2000, "a smooth, slowly-varying signal should stay well within the quantizer's adaptive step range once it has ramped up");
        }
    }
}
