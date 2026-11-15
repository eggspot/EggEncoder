using EggEncoder.Codecs.H264;
using EggEncoder.Media;
using EggEncoder.Video;
using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264DecoderIntraTest
    {
        public static TheoryData<string, string[]> FfmpegFrames => new()
        {
            { "intra_basic.h264", ["d8b0ec4c4559925598927052a6637dcd", "9caf44a2b7a46875c1ea20c568c464b2", "ab79e70e42602c53062ee38dce462f55"] },
            { "intra_slices.h264", ["5f625664baf41789e444018b371d4de9", "1b7d41d519d64d2eb20f5c22bac2ebcf", "07e070eba4474e77c6e775ff4cfa3cf0"] },
            { "intra_slices_idc2.h264", ["8e2c45f0e03e4d356f1b784c0f9ce875", "0d28052c0241965234f3dfb676c4acd3", "dbe2e8591e10983a3c9384fa300b0807"] },
            { "intra_nodeblock.h264", ["0b50ffd3dc437ecfbb25e94cd37a2900", "b814c4ea7faa85b83328477f9de3d043", "d9259ac340ecf9939e6938e15197ae09"] },
            { "intra_constrained.h264", ["7eca59817e62b22e8f8bc57333528432", "4fbd01d87886ec928bc198ca3940f507", "3f18c93b4f7caba1cd3c204762ab1fc4"] },
            { "intra_chromaqp.h264", ["9e53395afdb7d859a5ecfe6a2a89f7b1", "dfff29e3950001ceb0c655653503a40f", "59ad2cd917ec79bb99caccb740b3a60a"] },
            { "intra_cropped.h264", ["77d622662b22272e20d91fe7e919e3d8", "5d50eafe9d131c67f46103fc1c3f838f", "7dc544100c0efcb1c089464011152cef"] },
            { "intra_highqp.h264", ["0a414700ec333b3b9d3439c1d57659e6", "cd0e047408cf9fb66c54accc6864d93f", "9220ef0c07402622d11dab03ba3487c5"] },
            { "intra_lowqp.h264", ["265b92cefcac980193270a52df32b766", "164c15ad89e104cc7b128ef63eda0cff", "6de98b55ee0bff5c20b05c4ca3ac3fb7"] }
        };

        [Theory]
        [MemberData(nameof(FfmpegFrames))]
        public void Decode_Should_Match_Ffmpeg_Bit_For_Bit(string fixture, string[] expected)
        {
            DecodeToMd5(Fixture(fixture)).Should().Equal(expected);
        }

        [Fact]
        public void Decode_Should_Report_The_Cropped_Size_Colours_And_Timestamps()
        {
            using var decoder = new H264Decoder();
            var frames = new List<VideoFrame>();
            var units = AccessUnits(Fixture("intra_cropped.h264"));

            for (var i = 0; i < units.Count; i++)
            {
                decoder.Decode(units[i], TimeSpan.FromSeconds(i), frames.Add);
            }

            (decoder.Width, decoder.Height).Should().Be((66, 50));
            decoder.ColorMatrix.Should().Be(VideoColorMatrix.Bt601);
            decoder.ColorRange.Should().Be(VideoColorRange.Limited);
            frames.Select(f => (f.Width, f.Height, f.Timestamp)).Should().Equal((66, 50, TimeSpan.Zero), (66, 50, TimeSpan.FromSeconds(1)), (66, 50, TimeSpan.FromSeconds(2)));
            frames.ForEach(f => f.Dispose());
        }

        [Fact]
        public void Decode_From_Avcc_Should_Equal_Annex_B()
        {
            var annexB = Fixture("intra_basic.h264");
            var units = AccessUnits(annexB);
            var nalUnits = NalUnits(annexB).Select(n => annexB.AsSpan(n.Start, n.End - n.Start).ToArray()).ToList();
            var sps = nalUnits.First(n => (n[0] & 0x1F) == 7);
            var pps = nalUnits.First(n => (n[0] & 0x1F) == 8);
            var record = new AvcDecoderConfigurationRecord(sps[1], sps[2], sps[3], 4, [sps], [pps]).ToArray();
            using var decoder = new H264Decoder(record);
            var hashes = new List<string>();

            foreach (var unit in units)
            {
                decoder.Decode(AnnexBConverter.ToLengthPrefixed(unit), TimeSpan.Zero, f =>
                {
                    hashes.Add(Md5(f));
                    f.Dispose();
                });
            }

            hashes.Should().Equal(DecodeToMd5(annexB));
        }

        [Fact]
        public void IPcm_Macroblocks_Should_Come_Out_As_Their_Samples()
        {
            static byte Sample(int mb, int i) => (byte)((mb * 37) + (i * 11));
            var stream = Sps(3, 2).Concat(Pps()).Concat(PcmPicture(3, 2, 0, _ => true, Sample)).ToArray();
            using var decoder = new H264Decoder();
            VideoFrame? frame = null;

            decoder.Decode(stream, TimeSpan.Zero, f => frame = f);

            frame.Should().NotBeNull();
            for (var mb = 0; mb < 6; mb++)
            {
                var mbX = mb % 3;
                var mbY = mb / 3;
                frame!.GetRow(VideoPlane.Y, (mbY * 16) + 5)[(mbX * 16) + 7].Should().Be(Sample(mb, (5 * 16) + 7));
                frame.GetRow(VideoPlane.U, (mbY * 8) + 2)[(mbX * 8) + 3].Should().Be(Sample(mb, 256 + (2 * 8) + 3));
                frame.GetRow(VideoPlane.V, (mbY * 8) + 7)[(mbX * 8) + 7].Should().Be(Sample(mb, 320 + 63));
            }

            frame!.Dispose();
        }

        [Fact]
        public void IPcm_Mixed_With_Intra16x16_Should_Match_Ffmpeg()
        {
            static byte Sample(int mb, int i) => (byte)(40 + (mb * 13) + ((i * 7) % 90));
            var stream = Sps(4, 3).Concat(Pps()).Concat(PcmPicture(4, 3, 0, mb => mb % 3 == 0, Sample)).ToArray();
            var dump = Environment.GetEnvironmentVariable("EGGENCODER_H264_DUMP");
            if (dump is not null)
            {
                File.WriteAllBytes(dump, stream);
            }

            using var decoder = new H264Decoder();
            string? hash = null;
            decoder.Decode(stream, TimeSpan.Zero, f =>
            {
                hash = Md5(f);
                f.Dispose();
            });

            hash.Should().Be("86f8c70e4c410a7f360ceaa2ee8838e7");
        }

        [Theory]
        [InlineData(100, 10, true, 1, "10-bit")]
        [InlineData(100, 8, true, 2, "chroma format")]
        [InlineData(66, 8, false, 1, "interlaced")]
        public void Unsupported_Sequences_Should_Throw_NotSupported(int profile, int bitDepth, bool frameMbsOnly, int chromaFormat, string reason)
        {
            using var decoder = new H264Decoder();

            decoder.Invoking(d => d.Decode(Sps(4, 3, profile, bitDepth, frameMbsOnly, chromaFormat), TimeSpan.Zero, _ => { }))
                .Should().Throw<NotSupportedException>(reason);
        }

        [Fact]
        public void A_Slice_Without_Its_Parameter_Sets_Should_Throw_InvalidData()
        {
            var units = AccessUnits(Fixture("intra_basic.h264"));
            var sliceOnly = units[0].Skip(NalUnits(units[0]).Last().Start - 4).ToArray();
            using var decoder = new H264Decoder();

            decoder.Invoking(d => d.Decode(sliceOnly, TimeSpan.Zero, _ => { })).Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Cabac_Should_Throw_NotSupported_For_Now()
        {
            var stream = Sps(1, 1).Concat(Pps(cabac: true)).Concat(PcmPicture(1, 1, 0, _ => true, (_, _) => 0)).ToArray();
            using var decoder = new H264Decoder();

            decoder.Invoking(d => d.Decode(stream, TimeSpan.Zero, _ => { })).Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Damaged_Streams_Should_Only_Throw_InvalidData_Or_NotSupported()
        {
            var original = Fixture("intra_slices.h264");
            var random = new Random(42);
            for (var round = 0; round < 300; round++)
            {
                var bytes = (byte[])original.Clone();
                if (round % 3 == 0)
                {
                    bytes = bytes[..random.Next(bytes.Length)];
                }
                else
                {
                    for (var flips = 0; flips < 3; flips++)
                    {
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                    }
                }

                using var decoder = new H264Decoder();
                foreach (var unit in AccessUnits(bytes))
                {
                    try
                    {
                        decoder.Decode(unit, TimeSpan.Zero, f => f.Dispose());
                    }
                    catch (InvalidDataException)
                    {
                    }
                    catch (NotSupportedException)
                    {
                    }
                }
            }
        }

        [Fact]
        public void Disposed_Decoder_Should_Throw_And_Arguments_Should_Be_Checked()
        {
            var decoder = new H264Decoder();
            decoder.Invoking(d => d.Decode(Fixture("intra_basic.h264"), TimeSpan.Zero, null!)).Should().Throw<ArgumentNullException>();
            decoder.Invoking(d => d.Flush(null!)).Should().Throw<ArgumentNullException>();
            decoder.Dispose();
            decoder.Dispose();

            decoder.Invoking(d => d.Decode(Fixture("intra_basic.h264"), TimeSpan.Zero, _ => { })).Should().Throw<ObjectDisposedException>();
            decoder.Invoking(d => d.Flush(_ => { })).Should().Throw<ObjectDisposedException>();
            (decoder.Width, decoder.Height, decoder.FrameRate).Should().Be((0, 0, null));
        }
    }

    public class CavlcTablesTest
    {
        [Fact]
        public void Every_Coeff_Token_Column_Should_Be_A_Complete_Prefix_Free_Code()
        {
            foreach (var table in Enumerable.Range(0, 6))
            {
                var codes = CavlcTables.CoeffTokenEntries().Where(e => e.Table == table).Select(e => e.Code).ToList();
                codes.Should().OnlyHaveUniqueItems();
                foreach (var code in codes)
                {
                    codes.Where(other => other != code).Should().NotContain(other => other.StartsWith(code, StringComparison.Ordinal));
                }
            }

            CavlcTables.CoeffTokenEntries().Count().Should().Be((62 * 4) + 14 + 30);
        }

        [Fact]
        public void Bit_Reader_Should_Read_Exp_Golomb_And_Stop_At_The_End()
        {
            var reader = new H264BitReader([0b1_010_011_0, 0b0100_0010, 0b1000_0000]);

            reader.ReadUe().Should().Be(0);
            reader.ReadUe().Should().Be(1);
            reader.ReadUe().Should().Be(2);
            reader.ReadUe().Should().Be(3);
            reader.ReadSe().Should().Be(-2);
            reader.MoreRbspData().Should().BeFalse();
            reader.Invoking(r => r.ReadBits(16)).Should().Throw<InvalidDataException>();
        }
    }
}
