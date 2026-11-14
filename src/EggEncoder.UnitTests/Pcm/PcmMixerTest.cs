using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class PcmMixerTest
    {
        private static PcmAudio Mono(params float[] samples) => new(samples, 1, 1000);

        [Fact]
        public void Mix_Should_Sum_Inputs_With_Their_Gains_Without_Clipping()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(0.75f, 0.75f)), new PcmMixInput(Mono(0.5f, -0.5f), Gain: 2.0)], sampleRate: 1000, channels: 1);

            mix.Samples.Should().Equal(1.75f, -0.25f);
        }

        [Fact]
        public void Mix_StartOffset_Should_Delay_The_Input_By_Rounded_Frames()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(1f, 2f), StartOffset: TimeSpan.FromMilliseconds(2.6))], sampleRate: 1000, channels: 1);

            mix.Samples.Should().Equal(0f, 0f, 0f, 1f, 2f);
        }

        [Theory]
        [InlineData(PcmMixLength.Longest, 4)]
        [InlineData(PcmMixLength.Shortest, 2)]
        [InlineData(PcmMixLength.First, 3)]
        public void Mix_LengthRule_Should_Decide_The_Length(PcmMixLength rule, int expectedFrames)
        {
            var mix = PcmMixer.Mix(
                [new PcmMixInput(Mono(1f, 1f, 1f)), new PcmMixInput(Mono(1f, 1f)), new PcmMixInput(Mono(1f), StartOffset: TimeSpan.FromMilliseconds(3))],
                rule,
                sampleRate: 1000,
                channels: 1);

            mix.FrameCount.Should().Be(expectedFrames);
        }

        [Fact]
        public void Mix_LoopingInput_Should_Repeat_After_Its_Offset_Until_The_End()
        {
            var mix = PcmMixer.Mix(
                [new PcmMixInput(Mono(0f, 0f, 0f, 0f, 0f, 0f)), new PcmMixInput(Mono(1f, 2f), StartOffset: TimeSpan.FromMilliseconds(1), Loop: true)],
                PcmMixLength.First,
                sampleRate: 1000,
                channels: 1);

            mix.Samples.Should().Equal(0f, 1f, 2f, 1f, 2f, 1f);
        }

        [Fact]
        public void Mix_FixedLength_Should_Win_Over_The_Rule_With_A_Loop()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(1f, 2f, 3f), Loop: true)], PcmMixLength.Longest, TimeSpan.FromMilliseconds(5), 1000, 1);

            mix.Samples.Should().Equal(1f, 2f, 3f, 1f, 2f);
        }

        [Fact]
        public void Mix_FixedLength_Should_Cut_A_Longer_Input()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(1f, 2f, 3f))], fixedLength: TimeSpan.FromMilliseconds(2), sampleRate: 1000, channels: 1);

            mix.Samples.Should().Equal(1f, 2f);
        }

        [Theory]
        [InlineData(PcmMixLength.Longest)]
        [InlineData(PcmMixLength.First)]
        public void Mix_RuleThatNeverEnds_Should_Throw_Argument(PcmMixLength rule)
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f), Loop: true), new PcmMixInput(Mono(1f))], rule, sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Mix_ShortestWithEveryInputLooping_Should_Throw_Argument()
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f), Loop: true)], PcmMixLength.Shortest, sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Mix_ShortestWithOneFiniteInput_Should_End_With_It()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(1f), Loop: true), new PcmMixInput(Mono(1f, 1f, 1f))], PcmMixLength.Shortest, sampleRate: 1000, channels: 1);

            mix.Samples.Should().Equal(2f, 2f, 2f);
        }

        [Fact]
        public void Mix_EmptyLoopingInput_Should_Add_Nothing_And_End_At_Its_Offset()
        {
            var mix = PcmMixer.Mix([new PcmMixInput(Mono(), StartOffset: TimeSpan.FromMilliseconds(2), Loop: true)], sampleRate: 1000, channels: 1);

            mix.Samples.Should().Equal(0f, 0f);
        }

        [Fact]
        public void Mix_Should_Convert_Inputs_To_The_Mix_Format()
        {
            var stereo = new PcmAudio([0.2f, 0.4f], 2, 1000);

            var mix = PcmMixer.Mix([new PcmMixInput(stereo)], sampleRate: 1000, channels: 1);

            mix.Samples.Should().HaveCount(1);
            mix.Samples[0].Should().BeApproximately(0.3f, 1e-6f);
        }

        [Fact]
        public void Mix_NoInputs_Should_Throw_Argument()
        {
            var act = () => PcmMixer.Mix([]);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Mix_NullInputs_Should_Throw_ArgumentNull()
        {
            var act = () => PcmMixer.Mix(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Mix_DefaultInput_Should_Throw_ArgumentNull()
        {
            var act = () => PcmMixer.Mix([default]);

            act.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Mix_NonFiniteGain_Should_Throw_ArgumentOutOfRange(double gain)
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f), gain)], sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Mix_NegativeOffset_Should_Throw_ArgumentOutOfRange()
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f), StartOffset: TimeSpan.FromMilliseconds(-1))], sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Mix_NegativeFixedLength_Should_Throw_ArgumentOutOfRange()
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f))], fixedLength: TimeSpan.FromSeconds(-1), sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Mix_UnknownRule_Should_Throw_ArgumentOutOfRange()
        {
            var act = () => PcmMixer.Mix([new PcmMixInput(Mono(1f))], (PcmMixLength)9, sampleRate: 1000, channels: 1);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
