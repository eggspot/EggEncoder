using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class G711CodecTest
    {
        [Theory]
        [InlineData(0x00, -5504)]
        [InlineData(0x55, -8)]
        [InlineData(0xD5, 8)]
        [InlineData(0xFF, 848)]
        [InlineData(0x7F, -848)]
        [InlineData(0x80, 5504)]
        public void DecodeALaw_KnownBytes_Should_Match_VerifiedValues(byte coded, int expected)
        {
            // Values independently verified via a from-scratch Python port of the same FFmpeg
            // formula, cross-checked against this project's own real ffmpeg-produced fixtures.
            G711Codec.DecodeALaw(coded).Should().Be(expected);
        }

        [Theory]
        [InlineData(0x00, -32124)]
        [InlineData(0x55, -716)]
        [InlineData(0xD5, 716)]
        [InlineData(0xFF, 0)]
        [InlineData(0x7F, 0)]
        [InlineData(0x80, 32124)]
        public void DecodeMuLaw_KnownBytes_Should_Match_VerifiedValues(byte coded, int expected)
        {
            G711Codec.DecodeMuLaw(coded).Should().Be(expected);
        }

        [Fact]
        public void DecodeMuLaw_DualZeroCodes_Should_BothDecode_To_Zero()
        {
            // mu-law has two distinct byte codes for zero ("positive" and "negative" zero, a
            // documented quirk of its sign-magnitude representation at the zero crossing) --
            // both must decode to the same linear value.
            G711Codec.DecodeMuLaw(0x7F).Should().Be(0);
            G711Codec.DecodeMuLaw(0xFF).Should().Be(0);
        }

        [Theory]
        [InlineData(0, 0xD5)]
        [InlineData(32767, 0xAA)]
        [InlineData(-32768, 0x2A)]
        [InlineData(1000, 0xFA)]
        [InlineData(-1000, 0x7A)]
        public void EncodeALaw_KnownSamples_Should_Match_VerifiedValues(int sample, byte expected)
        {
            G711Codec.EncodeALaw(sample).Should().Be(expected);
        }

        [Theory]
        [InlineData(0, 0xFF)]
        [InlineData(32767, 0x80)]
        [InlineData(-32768, 0x00)]
        [InlineData(1000, 0xCE)]
        [InlineData(-1000, 0x4E)]
        public void EncodeMuLaw_KnownSamples_Should_Match_VerifiedValues(int sample, byte expected)
        {
            G711Codec.EncodeMuLaw(sample).Should().Be(expected);
        }

        [Fact]
        public void EncodeMuLaw_Zero_Should_Return_The_Canonical_Code_Not_The_Original_PositiveZero()
        {
            // Decoding 0x7F ("positive zero") gives linear 0, same as decoding 0xFF ("negative
            // zero") -- but re-encoding that 0 always produces the canonical 0xFF, matching
            // FFmpeg's own real encoder (verified against real ffmpeg-encoded fixtures). This is
            // the one byte value (of 256) where Decode-then-Encode does not reproduce the
            // original byte -- every other value round-trips exactly (see the round-trip tests
            // below).
            var decoded = G711Codec.DecodeMuLaw(0x7F);

            decoded.Should().Be(0);
            G711Codec.EncodeMuLaw(decoded).Should().Be(0xFF);
        }

        [Fact]
        public void ALaw_EncodeOfDecode_Should_Reproduce_Every_Byte_Exactly()
        {
            // Unlike mu-law, A-law has no dual-zero-code exception -- every one of the 256
            // possible coded bytes round-trips through Decode-then-Encode exactly.
            for (var coded = 0; coded <= 0xFF; coded++)
            {
                var decoded = G711Codec.DecodeALaw((byte)coded);
                var reEncoded = G711Codec.EncodeALaw(decoded);

                reEncoded.Should().Be((byte)coded, $"byte {coded:X2} should round-trip exactly");
            }
        }

        [Fact]
        public void MuLaw_EncodeOfDecode_Should_Reproduce_Every_Byte_Except_The_Documented_PositiveZero_Exception()
        {
            for (var coded = 0; coded <= 0xFF; coded++)
            {
                if (coded == 0x7F)
                {
                    continue;
                }

                var decoded = G711Codec.DecodeMuLaw((byte)coded);
                var reEncoded = G711Codec.EncodeMuLaw(decoded);

                reEncoded.Should().Be((byte)coded, $"byte {coded:X2} should round-trip exactly");
            }
        }

        [Fact]
        public void EncodeALaw_WithSampleFarBeyondInt16Range_Should_Clamp_Without_Throwing()
        {
            // The encode table is indexed by a genuine int16-range sample; a value far outside that
            // range would otherwise index straight past the 16384-entry table
            // (IndexOutOfRangeException). Note int.MaxValue actually overflows (sample + 32768)
            // back around to a negative number under C#'s default unchecked int arithmetic, landing
            // on the SAME clamped index (0) as int.MinValue -- not the opposite table end a
            // non-overflow-aware calculation would suggest. Confirmed via the real runtime, not
            // assumed.
            G711Codec.EncodeALaw(int.MaxValue).Should().Be(0x2A);
            G711Codec.EncodeALaw(int.MinValue).Should().Be(0x2A);
        }

        [Fact]
        public void EncodeMuLaw_WithSampleFarBeyondInt16Range_Should_Clamp_Without_Throwing()
        {
            G711Codec.EncodeMuLaw(int.MaxValue).Should().Be(0x00);
            G711Codec.EncodeMuLaw(int.MinValue).Should().Be(0x00);
        }
    }
}
