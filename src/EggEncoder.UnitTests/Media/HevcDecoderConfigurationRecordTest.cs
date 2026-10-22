using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class HevcDecoderConfigurationRecordTest
    {
        [Fact]
        public void Parse_Real1080pRecord_Should_Match_Ffprobe()
        {
            var record = HevcDecoderConfigurationRecord.Parse(MediaFixtures.Hevc1080pRecord);

            // ffprobe: profile=Main, level=120.
            record.ProfileTierLevel.ProfileSpace.Should().Be(0);
            record.ProfileTierLevel.TierFlag.Should().BeFalse();
            record.ProfileTierLevel.ProfileIdc.Should().Be(1);
            record.ProfileTierLevel.ProfileCompatibilityFlags.Should().Be(0x60000000);
            record.ProfileTierLevel.ConstraintIndicatorFlags.Should().Be(0x900000000000);
            record.ProfileTierLevel.LevelIdc.Should().Be(120);
            record.MinSpatialSegmentationIdc.Should().Be(0);
            record.ParallelismType.Should().Be(0);
            record.ChromaFormat.Should().Be(1);
            record.BitDepthLuma.Should().Be(8);
            record.BitDepthChroma.Should().Be(8);
            record.AverageFrameRate.Should().Be(0);
            record.ConstantFrameRate.Should().Be(0);
            record.NumTemporalLayers.Should().Be(1);
            record.TemporalIdNested.Should().BeTrue();
            record.NalLengthSize.Should().Be(4);
            record.Arrays.Select(a => a.NalUnitType).Should().Equal(32, 33, 34);
            record.Arrays.Should().OnlyContain(a => a.ArrayCompleteness);
            record.GetNalUnits(HevcDecoderConfigurationRecord.VpsNalUnitType).Should().ContainSingle();
            record.GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType).Should().ContainSingle()
                .Which.Span[0].Should().Be(0x42);
            record.GetNalUnits(HevcDecoderConfigurationRecord.PpsNalUnitType).Should().ContainSingle();
            record.GetNalUnits(39).Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void ParseThenToArray_RealRecord_Should_Be_ByteIdentical(int fixture)
        {
            var hvcC = fixture == 0 ? MediaFixtures.Hevc1080pRecord : MediaFixtures.Hevc100x60Record;

            HevcDecoderConfigurationRecord.Parse(hvcC).ToArray().Should().Equal(hvcC);
        }

        [Fact]
        public void ToArray_Built_From_Fields_Should_Parse_Back_Equal()
        {
            var record = new HevcDecoderConfigurationRecord(
                new HevcProfileTierLevel(1, true, 4, 0x82000000, 0xB02300000000, 153),
                MinSpatialSegmentationIdc: 4095,
                ParallelismType: 3,
                ChromaFormat: 3,
                BitDepthLuma: 10,
                BitDepthChroma: 12,
                AverageFrameRate: 6400,
                ConstantFrameRate: 1,
                NumTemporalLayers: 3,
                TemporalIdNested: false,
                NalLengthSize: 2,
                [
                    new HevcNalUnitArray(false, 33, [new byte[] { 0x42, 0x01 }, new byte[] { 0x42, 0x02 }]),
                    new HevcNalUnitArray(true, 39, [new byte[] { 0x4E, 0x01 }])
                ]);

            var bytes = record.ToArray();
            var parsed = HevcDecoderConfigurationRecord.Parse(bytes);

            parsed.ProfileTierLevel.Should().Be(record.ProfileTierLevel);
            parsed.MinSpatialSegmentationIdc.Should().Be(4095);
            parsed.ParallelismType.Should().Be(3);
            parsed.ChromaFormat.Should().Be(3);
            parsed.BitDepthLuma.Should().Be(10);
            parsed.BitDepthChroma.Should().Be(12);
            parsed.AverageFrameRate.Should().Be(6400);
            parsed.ConstantFrameRate.Should().Be(1);
            parsed.NumTemporalLayers.Should().Be(3);
            parsed.TemporalIdNested.Should().BeFalse();
            parsed.NalLengthSize.Should().Be(2);
            parsed.Arrays[0].ArrayCompleteness.Should().BeFalse();
            parsed.GetNalUnits(33).Should().HaveCount(2);
            parsed.GetNalUnits(39).Should().ContainSingle();
            parsed.ToArray().Should().Equal(bytes);
        }

        private static byte[] Header(int version = 1, int lengthSizeByte = 0x0F) =>
            [(byte)version, 0x01, 0x60, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 0x78, 0xF0, 0, 0xFC, 0xFD, 0xF8, 0xF8, 0, 0, (byte)lengthSizeByte];

        public static TheoryData<byte[], string> MalformedRecords => new()
        {
            { [], "*truncated*" },
            { [.. Header()], "*truncated*" },
            { [.. Header(version: 0), 0], "*unsupported configurationVersion 0*" },
            { [.. Header(lengthSizeByte: 0x0E), 0], "*lengthSizeMinusOne of 2*" },
            { [.. Header(), 0], "*no sequence parameter set*" },
            { [.. Header(), 1, 0xA0, 0, 1, 0, 2, 0x40, 0x01], "*no sequence parameter set*" },
            { [.. Header(), 1, 0xA1, 0, 1, 0, 0], "*zero-length NAL unit*" },
            { [.. Header(), 1, 0xA1, 0, 1, 0, 9, 0x42], "*truncated*" },
            { [.. Header(), 1, 0xA1, 0xFF, 0xFF, 0, 1, 0x42], "*truncated*" },
            { [.. Header(), 2, 0xA1, 0, 1, 0, 1, 0x42], "*truncated*" }
        };

        [Theory]
        [MemberData(nameof(MalformedRecords))]
        public void Parse_MalformedRecord_Should_Throw_InvalidDataException(byte[] hvcC, string message)
        {
            var act = () => HevcDecoderConfigurationRecord.Parse(hvcC);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Fact]
        public void Parse_EveryTruncationOfRealRecord_Should_Throw_InvalidDataException()
        {
            var hvcC = MediaFixtures.Hevc1080pRecord;
            for (var length = 0; length < hvcC.Length; length++)
            {
                var act = () => HevcDecoderConfigurationRecord.Parse(hvcC.AsSpan(0, length).ToArray());

                act.Should().ThrowExactly<InvalidDataException>($"a {length}-byte prefix is incomplete");
            }
        }

        public static TheoryData<HevcDecoderConfigurationRecord> InvalidRecords
        {
            get
            {
                var valid = HevcDecoderConfigurationRecord.Parse(MediaFixtures.Hevc1080pRecord);
                var ptl = valid.ProfileTierLevel;
                return new()
                {
                    valid with { ProfileTierLevel = ptl with { ProfileSpace = 4 } },
                    valid with { ProfileTierLevel = ptl with { ProfileIdc = 32 } },
                    valid with { ProfileTierLevel = ptl with { ConstraintIndicatorFlags = 1UL << 48 } },
                    valid with { ProfileTierLevel = ptl with { LevelIdc = 256 } },
                    valid with { NalLengthSize = 3 },
                    valid with { MinSpatialSegmentationIdc = 4096 },
                    valid with { ParallelismType = 4 },
                    valid with { ChromaFormat = -1 },
                    valid with { BitDepthLuma = 16 },
                    valid with { BitDepthChroma = 7 },
                    valid with { AverageFrameRate = 0x10000 },
                    valid with { ConstantFrameRate = 4 },
                    valid with { NumTemporalLayers = 8 },
                    valid with { Arrays = [new HevcNalUnitArray(true, 64, [new byte[] { 1 }])] },
                    valid with { Arrays = [new HevcNalUnitArray(true, 33, [ReadOnlyMemory<byte>.Empty])] },
                    valid with { Arrays = Enumerable.Repeat(valid.Arrays[0], 256).ToArray() }
                };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidRecords))]
        public void ToArray_FieldOutOfRange_Should_Throw_InvalidOperationException(HevcDecoderConfigurationRecord record)
        {
            var act = () => record.ToArray();

            act.Should().ThrowExactly<InvalidOperationException>();
        }
    }
}
