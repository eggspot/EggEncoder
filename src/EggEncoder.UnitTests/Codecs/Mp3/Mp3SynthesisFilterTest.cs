using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3SynthesisFilterTest
    {
        [Fact]
        public void Synthesize_WithWrongLength_Should_Throw()
        {
            var filter = new Mp3SynthesisFilter();

            var act = () => filter.Synthesize(new double[10]);

            act.Should().ThrowExactly<ArgumentException>();
        }

        // The meaningful correctness test for this type -- round-tripping a real signal through
        // the full hybrid-filter + frequency-inversion + synthesis-filter chain against the
        // existing, already-shipped encoder -- lives in Mp3HybridAndSynthesisRoundTripTest.cs
        // (currently [Fact(Skip=...)] -- see that test's own doc comment and this type's own class
        // doc comment for the open bug). (An earlier version of this test tried to pair
        // Mp3SynthesisFilter directly against Mp3PolyphaseFilter.Analyze's own raw subband output;
        // that pairing is wrong -- Analyze's output feeds the encoder's OWN hybrid filter/MDCT
        // stage across time, not Synthesize directly, so it couldn't actually validate anything
        // about this type.)

        [Fact]
        public void Synthesize_Should_BeDeterministic_ForTheSameInputHistory()
        {
            var filter = new Mp3SynthesisFilter();
            var subbandSamples = new double[32];
            subbandSamples[3] = 500.0;

            var first = filter.Synthesize(subbandSamples);
            var filterAgain = new Mp3SynthesisFilter();
            var second = filterAgain.Synthesize(subbandSamples);

            first.Should().Equal(second);
        }
    }
}
