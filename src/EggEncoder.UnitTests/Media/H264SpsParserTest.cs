using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class H264SpsParserTest
    {
        private static byte[] Sps(byte[] avcC) => AvcDecoderConfigurationRecord.Parse(avcC).SequenceParameterSets[0].ToArray();

        [Fact]
        public void Parse_RealMp4FixtureSps_Should_Match_Ffprobe()
        {
            var info = H264SpsParser.Parse(Sps(MediaFixtures.ReadMp4FixtureAvcC()));

            // ffprobe: High, level 30, 640x360 (coded as 23 macroblock rows = 368, cropped by 8).
            info.ProfileIdc.Should().Be(100);
            info.ConstraintFlags.Should().Be(0);
            info.LevelIdc.Should().Be(30);
            info.ChromaFormatIdc.Should().Be(1);
            info.BitDepthLuma.Should().Be(8);
            info.BitDepthChroma.Should().Be(8);
            info.Width.Should().Be(640);
            info.Height.Should().Be(360);
            info.CodecsString.Should().Be("avc1.64001E");
        }

        [Fact]
        public void Parse_Real1080pSps_Should_Apply_Cropping_From_1088_Rows()
        {
            // This SPS also contains an emulation-prevention byte (00 00 03), removed before parsing.
            var sps = Sps(MediaFixtures.Avc1080pRecord);
            sps.AsSpan().IndexOf(new byte[] { 0, 0, 3 }).Should().BePositive();

            var info = H264SpsParser.Parse(sps);

            info.Width.Should().Be(1920);
            info.Height.Should().Be(1080);
            info.CodecsString.Should().Be("avc1.640028");
        }

        [Fact]
        public void Parse_RealBaselineSps_Should_Default_Chroma_And_BitDepth()
        {
            var info = H264SpsParser.Parse(Sps(MediaFixtures.AvcBaselineRecord));

            info.ProfileIdc.Should().Be(66);
            info.ConstraintFlags.Should().Be(0xC0);
            info.ChromaFormatIdc.Should().Be(1);
            info.BitDepthLuma.Should().Be(8);
            info.Width.Should().Be(176);
            info.Height.Should().Be(144);
            info.CodecsString.Should().Be("avc1.42C00B");
        }

        [Fact]
        public void Parse_RealInterlacedSps_Should_Double_Height_And_Crop_In_Field_Units()
        {
            var info = H264SpsParser.Parse(Sps(MediaFixtures.AvcInterlacedRecord));

            info.Width.Should().Be(320);
            info.Height.Should().Be(240);
            info.CodecsString.Should().Be("avc1.640015");
        }

        // A synthetic SPS up to direct_8x8_inference_flag, for branches no real fixture reaches.
        private static RbspBuilder Prefix(int profileIdc, Action<RbspBuilder>? chromaFields = null, int pocType = 0)
        {
            var builder = new RbspBuilder(0x67).Bits((uint)profileIdc, 8).Bits(0, 8).Bits(31, 8).Ue(0);
            chromaFields?.Invoke(builder);
            builder.Ue(0).Ue((uint)pocType);
            if (pocType == 0)
            {
                builder.Ue(2);
            }
            else if (pocType == 1)
            {
                builder.Flag(false).Se(-3).Se(5).Ue(3).Se(1).Se(-1).Se(2);
            }

            return builder.Ue(4).Flag(false);
        }

        private static RbspBuilder Size(RbspBuilder builder, uint widthMbs, uint heightMapUnits, bool frameMbsOnly = true)
        {
            builder.Ue(widthMbs - 1).Ue(heightMapUnits - 1).Flag(frameMbsOnly);
            if (!frameMbsOnly)
            {
                builder.Flag(false);
            }

            return builder.Flag(true);
        }

        [Fact]
        public void Parse_PocType1AndScalingLists_Should_Skip_Them_And_Read_Size()
        {
            var builder = Prefix(100, b =>
            {
                b.Ue(1).Ue(2).Ue(2).Flag(false).Flag(true); // 4:2:0, 10-bit, scaling matrix present
                b.Flag(true); // list 0 present: 16 deltas ending in a zero nextScale
                b.Se(4);
                b.Se(-12); // nextScale = 0: stops
                b.Flag(false).Flag(false).Flag(false).Flag(false).Flag(false);
                b.Flag(true); // list 6 (8x8): full 64 entries, none zero
                for (var i = 0; i < 64; i++)
                {
                    b.Se(i == 0 ? 1 : 0);
                }

                b.Flag(false);
            }, pocType: 1);

            var info = H264SpsParser.Parse(Size(builder, 80, 45).Flag(false).Build());

            info.BitDepthLuma.Should().Be(10);
            info.BitDepthChroma.Should().Be(10);
            info.Width.Should().Be(1280);
            info.Height.Should().Be(720);
        }

        [Theory]
        [InlineData(0, false, 1, 1, 1, 1, 14, 14)] // monochrome: crop units 1x1
        [InlineData(1, false, 1, 1, 1, 1, 12, 12)] // 4:2:0: crop units 2x2
        [InlineData(2, false, 1, 1, 1, 1, 12, 14)] // 4:2:2: crop units 2x1
        [InlineData(3, false, 1, 1, 1, 1, 14, 14)] // 4:4:4: crop units 1x1
        [InlineData(3, true, 1, 1, 1, 1, 14, 14)] // separate colour planes: ChromaArrayType 0
        public void Parse_FrameCropping_Should_Use_Chroma_Format_Crop_Units(
            int chromaFormatIdc, bool separatePlanes, uint left, uint right, uint top, uint bottom, int width, int height)
        {
            var builder = Prefix(244, b =>
            {
                b.Ue((uint)chromaFormatIdc);
                if (chromaFormatIdc == 3)
                {
                    b.Flag(separatePlanes);
                }

                b.Ue(0).Ue(0).Flag(false).Flag(chromaFormatIdc == 3);
                if (chromaFormatIdc == 3)
                {
                    // 12 scaling lists for 4:4:4; none present.
                    for (var i = 0; i < 12; i++)
                    {
                        b.Flag(false);
                    }
                }
            });

            var sps = Size(builder, 1, 1).Flag(true).Ue(left).Ue(right).Ue(top).Ue(bottom).Build();
            var info = H264SpsParser.Parse(sps);

            info.ChromaFormatIdc.Should().Be(chromaFormatIdc);
            info.Width.Should().Be(width);
            info.Height.Should().Be(height);
        }

        [Fact]
        public void Parse_PocType2_Should_Have_No_Poc_Fields()
        {
            var info = H264SpsParser.Parse(Size(Prefix(66, pocType: 2), 2, 3).Flag(false).Build());

            info.Width.Should().Be(32);
            info.Height.Should().Be(48);
        }

        public static TheoryData<byte[], string> MalformedSps => new()
        {
            { [], "*truncated*" },
            { [0x67, 0x64, 0x00], "*truncated*" },
            { [0x68, 0x64, 0x00, 0x1E, 0xAC], "*NAL unit type 8 is not an H.264 SPS*" },
            { [0x67, 0x64, 0x00, 0x1E], "*truncated*" },
            { [0x67, 0x64, 0x00, 0x1E, 0, 0, 0, 0, 0, 0], "*Exp-Golomb code longer than 32 bits*" },
            { Prefix(100, b => b.Ue(4)).Build(), "*chroma_format_idc is 4*" },
            { Prefix(100, b => b.Ue(1).Ue(7)).Build(), "*bit_depth_luma_minus8 is 7*" },
            { Prefix(66, pocType: 3).Build(), "*pic_order_cnt_type is 3*" },
            { new RbspBuilder(0x67).Bits(66, 8).Bits(0, 8).Bits(31, 8).Ue(32).Build(), "*seq_parameter_set_id is 32*" },
            { new RbspBuilder(0x67).Bits(66, 8).Bits(0, 8).Bits(31, 8).Ue(0).Ue(0).Ue(1).Flag(false).Se(0).Se(0).Ue(256).Build(), "*num_ref_frames_in_pic_order_cnt_cycle is 256*" },
            { Size(Prefix(66), 1, 1).Flag(true).Ue(8).Ue(0).Ue(0).Ue(0).Build(), "*invalid picture size 0x16*" },
            { Size(Prefix(66), 1, 1).Flag(true).Ue(0).Ue(0).Ue(0).Ue(100).Build(), "*invalid picture size 16x-184*" },
            { Size(Prefix(66), 1, 1).Flag(true).Ue(0).BuildWithoutTrailingBits(), "*truncated*" }
        };

        [Theory]
        [MemberData(nameof(MalformedSps))]
        public void Parse_MalformedSps_Should_Throw_InvalidDataException(byte[] sps, string message)
        {
            var act = () => H264SpsParser.Parse(sps);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage(message);
        }

        [Fact]
        public void Parse_EveryTruncationAndRandomCorruptionOfRealSps_Should_Throw_InvalidDataException_Or_Parse()
        {
            var sps = Sps(MediaFixtures.Avc1080pRecord);
            for (var length = 0; length < sps.Length; length++)
            {
                AssertCleanOutcome(sps[..length]);
            }

            var random = new Random(42);
            for (var i = 0; i < 2000; i++)
            {
                var corrupted = (byte[])sps.Clone();
                corrupted[random.Next(1, corrupted.Length)] = (byte)random.Next(256);
                AssertCleanOutcome(corrupted);

                var noise = new byte[random.Next(1, 40)];
                random.NextBytes(noise);
                noise[0] = 0x67;
                AssertCleanOutcome(noise);
            }
        }

        private static void AssertCleanOutcome(byte[] sps)
        {
            try
            {
                var info = H264SpsParser.Parse(sps);
                info.Width.Should().BePositive();
                info.Height.Should().BePositive();
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
