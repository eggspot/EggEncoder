using EggEncoder.Codecs.Tta;
using EggEncoder.Transform;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaChannelStateTest
    {
        [Fact]
        public void Encode_Then_Decode_Should_RoundTrip_ThroughAllThreeStages()
        {
            var samples = new[] { 100, -200, 300, -400, 500, 0, -1, 32767, -32768 };

            var writer = new BitWriter();
            var encodeState = new TtaChannelState(filterShift: 9);
            foreach (var sample in samples)
            {
                encodeState.Encode(writer, sample);
            }

            var reader = new BitReader(writer.ToArray());
            var decodeState = new TtaChannelState(filterShift: 9);
            var decoded = new int[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                decoded[i] = decodeState.Decode(reader);
            }

            decoded.Should().Equal(samples);
        }
    }
}
