using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class H265SpsParserTest
    {
        private static byte[] Sps(byte[] hvcC) =>
            HevcDecoderConfigurationRecord.Parse(hvcC).GetNalUnits(HevcDecoderConfigurationRecord.SpsNalUnitType)[0].ToArray();

        [Fact]
        public void Parse_Real1080pSps_Should_Match_Ffprobe_And_The_Record()
        {
            var info = H265SpsParser.Parse(Sps(MediaFixtures.Hevc1080pRecord));

            info.Width.Should().Be(1920);
            info.Height.Should().Be(1080);
            info.ChromaFormatIdc.Should().Be(1);
            info.BitDepthLuma.Should().Be(8);
            info.BitDepthChroma.Should().Be(8);
            info.ProfileTierLevel.Should().Be(HevcDecoderConfigurationRecord.Parse(MediaFixtures.Hevc1080pRecord).ProfileTierLevel);
            info.CodecsString.Should().Be("hvc1.1.6.L120.90");
        }

        [Fact]
        public void Parse_Real100x60Sps_Should_Apply_Conformance_Window()
        {
            // ffprobe: coded_height=64, height=60.
            var info = H265SpsParser.Parse(Sps(MediaFixtures.Hevc100x60Record));

            info.Width.Should().Be(100);
            info.Height.Should().Be(60);
            info.CodecsString.Should().Be("hvc1.1.6.L30.90");
        }

        // Synthetic SPS: NAL header, then sps_video_parameter_set_id / max_sub_layers / nesting,
        // a profile_tier_level, then the fields up to bit depth.
        private static RbspBuilder Ptl(int maxSubLayersMinus1, bool[]? profilePresent = null, bool[]? levelPresent = null)
        {
            var builder = new RbspBuilder(0x42, 0x01)
                .Bits(0, 4).Bits((uint)maxSubLayersMinus1, 3).Flag(true)
                .Bits(0, 2).Flag(false).Bits(2, 5).Bits(0x20000000, 32).Bits(0x8000, 16).Bits(0, 32).Bits(93, 8);

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                builder.Flag(profilePresent?[i] ?? false).Flag(levelPresent?[i] ?? false);
            }

            if (maxSubLayersMinus1 > 0)
            {
                builder.Bits(0, 2 * (8 - maxSubLayersMinus1));
            }

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent?[i] ?? false)
                {
                    builder.Bits(0x3FFFFF, 22).Bits(0xFFFFFFFF, 32).Bits(0xFFFFFFFF, 32).Bits(0, 2);
                }

                if (levelPresent?[i] ?? false)
                {
                    builder.Bits(0xFF, 8);
                }
            }

            return builder;
        }

        [Fact]
        public void Parse_SubLayers_Should_Skip_Their_Profile_And_Level()
        {
            var sps = Ptl(3, [true, false, true], [true, true, false])
                .Ue(0).Ue(1).Ue(1280).Ue(720).Flag(false).Ue(2).Ue(2).Build();

            var info = H265SpsParser.Parse(sps);

            info.ProfileTierLevel.ProfileIdc.Should().Be(2);
            info.ProfileTierLevel.LevelIdc.Should().Be(93);
            info.ProfileTierLevel.ConstraintIndicatorFlags.Should().Be(0x800000000000);
            info.Width.Should().Be(1280);
            info.Height.Should().Be(720);
            info.BitDepthLuma.Should().Be(10);
            info.BitDepthChroma.Should().Be(10);
            info.CodecsString.Should().Be("hvc1.2.4.L93.80");
        }

        [Theory]
        [InlineData(0, false, 62, 62)] // monochrome: units 1x1
        [InlineData(1, false, 60, 60)] // 4:2:0: units 2x2
        [InlineData(2, false, 60, 62)] // 4:2:2: units 2x1
        [InlineData(3, false, 62, 62)] // 4:4:4: units 1x1
        [InlineData(3, true, 62, 62)] // separate colour planes
        public void Parse_ConformanceWindow_Should_Use_Chroma_Format_Units(int chromaFormatIdc, bool separatePlanes, int width, int height)
        {
            var builder = Ptl(0).Ue(0).Ue((uint)chromaFormatIdc);
            if (chromaFormatIdc == 3)
            {
                builder.Flag(separatePlanes);
            }

            var info = H265SpsParser.Parse(builder.Ue(64).Ue(64).Flag(true).Ue(1).Ue(1).Ue(1).Ue(1).Ue(0).Ue(0).Build());

            info.ChromaFormatIdc.Should().Be(chromaFormatIdc);
            info.Width.Should().Be(width);
            info.Height.Should().Be(height);
        }

        public static TheoryData<byte[], string> MalformedSps => new()
        {
            { [], "*truncated*" },
            { [0x42, 0x01], "*truncated*" },
            { [0x40, 0x01, 0x0C], "*NAL unit type 32 is not an H.265 SPS*" },
            { [0x42, 0x01, 0x01], "*truncated*" },
            { [0x42, 0x01, 0x0F, 0], "*sps_max_sub_layers_minus1 is 7*" },
            { Ptl(0).Ue(16).Build(), "*sps_seq_parameter_set_id is 16*" },
            { Ptl(0).Ue(0).Ue(4).Build(), "*chroma_format_idc is 4*" },
            { Ptl(0).Ue(0).Ue(1).Ue(64).Ue(64).Flag(false).Ue(9).Build(), "*bit_depth_luma_minus8 is 9*" },
            { Ptl(0).Ue(0).Ue(1).Ue(0).Ue(64).Flag(false).Ue(0).Ue(0).Build(), "*invalid picture size 0x64*" },
            { Ptl(0).Ue(0).Ue(1).Ue(64).Ue(64).Flag(true).Ue(0).Ue(0).Ue(40).Ue(0).Ue(0).Ue(0).Build(), "*invalid picture size 64x-16*" },
            { Ptl(0).Ue(0).Ue(1).Ue(64).BuildWithoutTrailingBits(), "*truncated*" }
        };

        [Theory]
        [MemberData(nameof(MalformedSps))]
        public void Parse_MalformedSps_Should_Throw_InvalidDataException(byte[] sps, string message)
        {
            var act = () => H265SpsParser.Parse(sps);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Fact]
        public void Parse_EveryTruncationAndRandomCorruptionOfRealSps_Should_Throw_InvalidDataException_Or_Parse()
        {
            var sps = Sps(MediaFixtures.Hevc1080pRecord);
            for (var length = 0; length < sps.Length; length++)
            {
                AssertCleanOutcome(sps[..length]);
            }

            var random = new Random(7);
            for (var i = 0; i < 2000; i++)
            {
                var corrupted = (byte[])sps.Clone();
                corrupted[random.Next(2, corrupted.Length)] = (byte)random.Next(256);
                AssertCleanOutcome(corrupted);

                var noise = new byte[random.Next(2, 60)];
                random.NextBytes(noise);
                noise[0] = 0x42;
                AssertCleanOutcome(noise);
            }
        }

        private static void AssertCleanOutcome(byte[] sps)
        {
            try
            {
                var info = H265SpsParser.Parse(sps);
                info.Width.Should().BePositive();
                info.Height.Should().BePositive();
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
