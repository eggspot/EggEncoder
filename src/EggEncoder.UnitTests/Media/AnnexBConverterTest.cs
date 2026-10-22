using EggEncoder.Codecs.Mov;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class AnnexBConverterTest
    {
        [Fact]
        public void RoundTrip_EveryAccessUnitOfRealH264Fixture_Should_Be_ByteIdentical()
        {
            var fileBytes = File.ReadAllBytes(MediaFixtures.Mp4FixturePath);
            var track = MovVideoDemuxer.DemuxVideoTrack(MediaFixtures.Mp4FixturePath);
            track.Samples.Should().HaveCount(125);

            foreach (var sample in track.Samples)
            {
                var avcc = fileBytes.AsSpan((int)sample.Offset, sample.Size);

                var annexB = AnnexBConverter.ToAnnexB(avcc);
                var back = AnnexBConverter.ToLengthPrefixed(annexB);

                annexB.AsSpan(0, 4).ToArray().Should().Equal(0, 0, 0, 1);
                back.Should().Equal(avcc.ToArray());
            }
        }

        [Fact]
        public void ToAnnexB_TwoNalUnits_Should_Replace_Each_Length_With_A_StartCode()
        {
            byte[] avcc = [0, 0, 0, 2, 0x09, 0xF0, 0, 0, 0, 3, 0x65, 0x88, 0x84];

            AnnexBConverter.ToAnnexB(avcc).Should().Equal(0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65, 0x88, 0x84);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void RoundTrip_ShortLengthPrefixes_Should_Be_ByteIdentical(int nalLengthSize)
        {
            byte[] avcc = nalLengthSize == 1
                ? [2, 0x67, 0x42, 1, 0x68]
                : [0, 2, 0x67, 0x42, 0, 1, 0x68];

            var annexB = AnnexBConverter.ToAnnexB(avcc, nalLengthSize);

            annexB.Should().Equal(0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68);
            AnnexBConverter.ToLengthPrefixed(annexB, nalLengthSize).Should().Equal(avcc);
        }

        [Fact]
        public void ToLengthPrefixed_ThreeByteStartCodesAndTrailingZeros_Should_Drop_The_Zeros()
        {
            // A leading zero_byte, 3-byte start codes, and trailing_zero_8bits after each NAL unit.
            byte[] annexB = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x01, 0, 0];

            AnnexBConverter.ToLengthPrefixed(annexB).Should().Equal(
                0, 0, 0, 2, 0x67, 0x42, 0, 0, 0, 2, 0x68, 0xCE, 0, 0, 0, 2, 0x65, 0x01);
        }

        [Fact]
        public void ToLengthPrefixed_EmulationPreventedZeros_Should_Not_Be_Mistaken_For_A_StartCode()
        {
            byte[] annexB = [0, 0, 1, 0x65, 0, 0, 3, 1, 0x80];

            AnnexBConverter.ToLengthPrefixed(annexB).Should().Equal(0, 0, 0, 6, 0x65, 0, 0, 3, 1, 0x80);
        }

        [Fact]
        public void ToLengthPrefixed_EmptyNalBetweenStartCodes_Should_Be_Skipped()
        {
            byte[] annexB = [0, 0, 1, 0, 0, 1, 0x09, 0x10];

            AnnexBConverter.ToLengthPrefixed(annexB).Should().Equal(0, 0, 0, 2, 0x09, 0x10);
        }

        [Theory]
        [InlineData(new byte[0], "*no start code*")]
        [InlineData(new byte[] { 0, 0 }, "*no start code*")]
        [InlineData(new byte[] { 0x65, 0, 0, 1, 0x65 }, "*does not start with a start code*")]
        [InlineData(new byte[] { 0x65, 0x88 }, "*does not start with a start code*")]
        [InlineData(new byte[] { 0, 0, 0, 1 }, "*no NAL units*")]
        [InlineData(new byte[] { 0, 0, 1, 0, 0, 0, 1, 0, 0 }, "*no NAL units*")]
        public void ToLengthPrefixed_MalformedInput_Should_Throw_InvalidDataException(byte[] annexB, string message)
        {
            var act = () => AnnexBConverter.ToLengthPrefixed(annexB);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Fact]
        public void ToLengthPrefixed_NalTooLongForPrefix_Should_Throw_InvalidDataException()
        {
            var annexB = new byte[3 + 256];
            annexB[2] = 1;
            annexB.AsSpan(3).Fill(0x41);

            var act = () => AnnexBConverter.ToLengthPrefixed(annexB, nalLengthSize: 1);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*does not fit a 1-byte length prefix*");
            AnnexBConverter.ToLengthPrefixed(annexB, nalLengthSize: 2).Should().HaveCount(2 + 256);
        }

        [Theory]
        [InlineData(new byte[0], "*no NAL units*")]
        [InlineData(new byte[] { 0, 0, 0 }, "*Truncated 4-byte NAL length prefix*")]
        [InlineData(new byte[] { 0, 0, 0, 0, 0x65 }, "*Zero-length NAL unit*")]
        [InlineData(new byte[] { 0, 0, 0, 5, 0x65 }, "*runs past the end*")]
        [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x65 }, "*runs past the end*")]
        [InlineData(new byte[] { 0, 0, 0, 1, 0x65, 0, 0 }, "*Truncated*")]
        public void ToAnnexB_MalformedInput_Should_Throw_InvalidDataException(byte[] avcc, string message)
        {
            var act = () => AnnexBConverter.ToAnnexB(avcc);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        public void Convert_InvalidNalLengthSize_Should_Throw(int nalLengthSize)
        {
            var toAnnexB = () => AnnexBConverter.ToAnnexB([1, 0x65], nalLengthSize);
            var toLengthPrefixed = () => AnnexBConverter.ToLengthPrefixed([0, 0, 1, 0x65], nalLengthSize);

            toAnnexB.Should().ThrowExactly<ArgumentOutOfRangeException>();
            toLengthPrefixed.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Convert_RandomBytes_Should_Either_Succeed_Or_Throw_InvalidDataException()
        {
            var random = new Random(1234);
            for (var i = 0; i < 2000; i++)
            {
                var data = new byte[random.Next(0, 64)];
                random.NextBytes(data);
                if (random.Next(2) == 0 && data.Length > 4)
                {
                    data[0] = data[1] = 0;
                    data[2] = (byte)random.Next(3);
                }

                foreach (var nalLengthSize in new[] { 1, 2, 4 })
                {
                    AssertCleanOutcome(() => AnnexBConverter.ToAnnexB(data, nalLengthSize));
                    AssertCleanOutcome(() => AnnexBConverter.ToLengthPrefixed(data, nalLengthSize));
                }
            }
        }

        private static void AssertCleanOutcome(Func<byte[]> convert)
        {
            try
            {
                convert().Should().NotBeEmpty();
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
