using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class AvcDecoderConfigurationRecordTest
    {
        [Fact]
        public void Parse_RealMp4FixtureAvcC_Should_Match_Ffprobe()
        {
            var record = AvcDecoderConfigurationRecord.Parse(MediaFixtures.ReadMp4FixtureAvcC());

            // ffprobe: profile=High (100), level=30, nal_length_size=4.
            record.ProfileIndication.Should().Be(100);
            record.ProfileCompatibility.Should().Be(0);
            record.LevelIndication.Should().Be(30);
            record.NalLengthSize.Should().Be(4);
            record.SequenceParameterSets.Should().ContainSingle();
            record.SequenceParameterSets[0].Span[0].Should().Be(0x67);
            record.PictureParameterSets.Should().ContainSingle();
            record.PictureParameterSets[0].Span[0].Should().Be(0x68);
            record.HighProfileExtension.Should().NotBeNull();
            record.HighProfileExtension!.ChromaFormat.Should().Be(1);
            record.HighProfileExtension.BitDepthLuma.Should().Be(8);
            record.HighProfileExtension.BitDepthChroma.Should().Be(8);
            record.HighProfileExtension.SequenceParameterSetExtensions.Should().BeEmpty();
        }

        public static TheoryData<byte[]> RealRecords => new()
        {
            MediaFixtures.ReadMp4FixtureAvcC(),
            MediaFixtures.Avc1080pRecord,
            MediaFixtures.AvcBaselineRecord,
            MediaFixtures.AvcInterlacedRecord
        };

        [Theory]
        [MemberData(nameof(RealRecords))]
        public void ParseThenToArray_RealRecord_Should_Be_ByteIdentical(byte[] avcC)
        {
            AvcDecoderConfigurationRecord.Parse(avcC).ToArray().Should().Equal(avcC);
        }

        [Fact]
        public void Parse_BaselineRecord_Should_Have_No_HighProfileExtension()
        {
            var record = AvcDecoderConfigurationRecord.Parse(MediaFixtures.AvcBaselineRecord);

            record.ProfileIndication.Should().Be(66);
            record.ProfileCompatibility.Should().Be(0xC0);
            record.LevelIndication.Should().Be(11);
            record.HighProfileExtension.Should().BeNull();
        }

        [Fact]
        public void Parse_HighProfileRecordEndingAfterPps_Should_Have_No_HighProfileExtension()
        {
            var avcC = MediaFixtures.Avc1080pRecord[..^4];

            var record = AvcDecoderConfigurationRecord.Parse(avcC);

            record.HighProfileExtension.Should().BeNull();
            record.ToArray().Should().Equal(avcC);
        }

        [Theory]
        [InlineData(100, true)]
        [InlineData(110, true)]
        [InlineData(122, true)]
        [InlineData(144, true)]
        [InlineData(244, true)]
        [InlineData(118, true)]
        [InlineData(66, false)]
        [InlineData(77, false)]
        [InlineData(88, false)]
        public void Parse_TrailingBytes_Should_Be_The_Extension_For_Every_Profile_But_66_77_88(int profile, bool hasExtension)
        {
            byte[] avcC = [1, (byte)profile, 0, 30, 0xFF, 0xE1, 0, 1, 0x67, 1, 0, 1, 0x68, 0xFD, 0xF9, 0xFA, 0];

            var record = AvcDecoderConfigurationRecord.Parse(avcC);

            (record.HighProfileExtension is not null).Should().Be(hasExtension);
            if (hasExtension)
            {
                record.HighProfileExtension!.BitDepthLuma.Should().Be(9);
                record.HighProfileExtension.BitDepthChroma.Should().Be(10);
                record.ToArray().Should().Equal(avcC);
            }
        }

        [Fact]
        public void Parse_Should_Copy_NalUnits_Rather_Than_Alias_The_Input()
        {
            var avcC = (byte[])MediaFixtures.Avc1080pRecord.Clone();
            var record = AvcDecoderConfigurationRecord.Parse(avcC);

            avcC.AsSpan().Fill(0);

            record.SequenceParameterSets[0].Span[0].Should().Be(0x67);
        }

        [Fact]
        public void ToArray_Built_From_Fields_Should_Parse_Back_Equal()
        {
            var record = new AvcDecoderConfigurationRecord(
                100, 0, 31, 2,
                [new byte[] { 0x67, 0x64, 0x00, 0x1F }, new byte[] { 0x67, 0x01 }],
                [new byte[] { 0x68, 0xEE }],
                new AvcHighProfileExtension(2, 10, 9, [new byte[] { 0x6D, 0x01 }]));

            var bytes = record.ToArray();
            var parsed = AvcDecoderConfigurationRecord.Parse(bytes);

            bytes[4].Should().Be(0xFD, "reserved bits are written as ones");
            bytes[5].Should().Be(0xE2);
            parsed.NalLengthSize.Should().Be(2);
            parsed.SequenceParameterSets.Should().HaveCount(2);
            parsed.HighProfileExtension!.ChromaFormat.Should().Be(2);
            parsed.HighProfileExtension.BitDepthLuma.Should().Be(10);
            parsed.HighProfileExtension.BitDepthChroma.Should().Be(9);
            parsed.HighProfileExtension.SequenceParameterSetExtensions[0].ToArray().Should().Equal(0x6D, 0x01);
            parsed.ToArray().Should().Equal(bytes);
        }

        public static TheoryData<byte[], string> MalformedRecords => new()
        {
            { [], "*truncated*" },
            { [2, 100, 0, 30, 0xFF, 0xE1], "*unsupported configurationVersion 2*" },
            { [1, 100, 0, 30, 0xFE, 0xE1], "*lengthSizeMinusOne of 2*" },
            { [1, 100, 0, 30, 0xFF, 0xE0, 0], "*no sequence parameter set*" },
            { [1, 100, 0, 30, 0xFF, 0xE1, 0, 9, 0x67], "*truncated*" },
            { [1, 100, 0, 30, 0xFF, 0xFF, 0, 1, 0x67], "*31 NAL units can't fit in the 3 bytes left*" },
            { [1, 100, 0, 30, 0xFF, 0xE1, 0, 0, 0x68], "*zero-length NAL unit*" },
            { [1, 100, 0, 30, 0xFF, 0xE1, 0, 1, 0x67], "*truncated*" },
            { [1, 100, 0, 30, 0xFF, 0xE1, 0, 1, 0x67, 1, 0, 1, 0x68, 0xFD], "*truncated*" },
            { [1, 100, 0, 30, 0xFF, 0xE1, 0, 1, 0x67, 1, 0, 1, 0x68, 0xFD, 0xF8, 0xF8, 1, 0, 5], "*truncated*" }
        };

        [Theory]
        [MemberData(nameof(MalformedRecords))]
        public void Parse_MalformedRecord_Should_Throw_InvalidDataException(byte[] avcC, string message)
        {
            var act = () => AvcDecoderConfigurationRecord.Parse(avcC);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Fact]
        public void Parse_EveryTruncationOfRealRecord_Should_Throw_InvalidDataException_Or_Parse()
        {
            var avcC = MediaFixtures.Avc1080pRecord;
            for (var length = 0; length < avcC.Length; length++)
            {
                try
                {
                    AvcDecoderConfigurationRecord.Parse(avcC.AsSpan(0, length));
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        public static TheoryData<AvcDecoderConfigurationRecord> InvalidRecords
        {
            get
            {
                var sps = new ReadOnlyMemory<byte>[] { new byte[] { 0x67 } };
                var pps = new ReadOnlyMemory<byte>[] { new byte[] { 0x68 } };
                var valid = new AvcDecoderConfigurationRecord(100, 0, 30, 4, sps, pps);
                return new()
                {
                    valid with { NalLengthSize = 3 },
                    valid with { ProfileIndication = 256 },
                    valid with { LevelIndication = -1 },
                    valid with { SequenceParameterSets = [] },
                    valid with { SequenceParameterSets = Enumerable.Repeat(sps[0], 32).ToArray() },
                    valid with { PictureParameterSets = Enumerable.Repeat(pps[0], 256).ToArray() },
                    valid with { PictureParameterSets = [ReadOnlyMemory<byte>.Empty] },
                    valid with { PictureParameterSets = [new byte[0x10000]] },
                    valid with { HighProfileExtension = new AvcHighProfileExtension(4, 8, 8, []) },
                    valid with { HighProfileExtension = new AvcHighProfileExtension(1, 7, 8, []) },
                    valid with { HighProfileExtension = new AvcHighProfileExtension(1, 8, 16, []) }
                };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidRecords))]
        public void ToArray_FieldOutOfRange_Should_Throw_InvalidOperationException(AvcDecoderConfigurationRecord record)
        {
            var act = () => record.ToArray();

            act.Should().ThrowExactly<InvalidOperationException>();
        }
    }
}
