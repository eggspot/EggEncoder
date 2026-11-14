using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Mp4
{
    public class Mp4FileSourceTest
    {
        public static readonly string H264AacFixture = Path.GetFullPath("Containers/Mp4/h264_aac_rotated.mp4");
        public static readonly string HevcFixture = Path.GetFullPath("Containers/Mp4/hevc.mp4");
        public static readonly string Vp9AacFixture = Path.GetFullPath("Containers/Mp4/vp9_aac.mp4");

        public static readonly (long Pts, long Dts, int Size, bool Key)[] FfprobeVideoPackets =
        [
            (0, -1024, 1924, true), (1536, -512, 30, false), (512, 0, 12, false), (1024, 512, 12, false),
            (2048, 1024, 21, false), (2560, 1536, 603, true), (4096, 2048, 31, false), (3072, 2560, 12, false),
            (3584, 3072, 12, false), (4608, 3584, 21, false)
        ];

        public static readonly (long Pts, int Size)[] FfprobeAudioPackets =
        [
            (-1024, 133), (0, 230), (1024, 153), (2048, 142), (3072, 145), (4096, 170), (5120, 156), (6144, 196),
            (7168, 185), (8192, 201), (9216, 190), (10240, 166), (11264, 226), (12288, 202), (13312, 200),
            (14336, 183), (15360, 168), (16384, 202), (17408, 7)
        ];

        [Fact]
        public void Tracks_H264AacFixture_Should_Match_Ffprobe()
        {
            using var source = Mp4FileSource.Open(H264AacFixture);

            source.Tracks.Tracks.Should().HaveCount(2);
            var video = source.Tracks.Tracks[0];
            video.TrackId.Should().Be(1);
            video.Kind.Should().Be(MediaKind.Video);
            video.Codec.Should().Be(MediaCodec.H264);
            video.Timescale.Should().Be(12800);
            video.Width.Should().Be(64);
            video.Height.Should().Be(48);
            video.Rotation.Should().Be(270);
            AvcDecoderConfigurationRecord.Parse(video.CodecConfiguration.Span).NalLengthSize.Should().Be(4);

            var audio = source.Tracks.Tracks[1];
            audio.TrackId.Should().Be(2);
            audio.Codec.Should().Be(MediaCodec.Aac);
            audio.Timescale.Should().Be(44100);
            audio.SampleRate.Should().Be(44100);
            audio.Channels.Should().Be(2);
            audio.CodecConfiguration.ToArray().Should().Equal(0x12, 0x10, 0x56, 0xE5, 0x00);

            source.Duration.Should().Be(TimeSpan.FromSeconds(0.4));
            source.SkippedTrackCount.Should().Be(0);
            source.GetSampleCount(1).Should().Be(10);
            source.GetSampleCount(2).Should().Be(19);
        }

        [Fact]
        public void ReadPackets_H264AacFixture_Should_Match_Ffprobe_Per_Track_And_Interleave_By_Decode_Time()
        {
            using var source = Mp4FileSource.Open(H264AacFixture);

            var packets = source.ReadPackets().ToList();

            packets.Where(p => p.TrackId == 1).Select(p => (p.Pts, p.Dts, p.Payload.Length, p.IsKeyframe))
                .Should().Equal(FfprobeVideoPackets);
            packets.Where(p => p.TrackId == 2).Select(p => (p.Pts, p.Payload.Length))
                .Should().Equal(FfprobeAudioPackets);
            packets.Where(p => p.TrackId == 2).Should().OnlyContain(p => p.IsKeyframe && p.Pts == p.Dts);
            var seconds = packets.Select(p => p.Dts / (double)(p.TrackId == 1 ? 12800 : 44100)).ToList();
            seconds.Should().BeInAscendingOrder();
            packets[0].Payload.Span[..4].ToArray().Should().NotEqual(new byte[] { 0, 0, 0, 1 });
        }

        [Fact]
        public async Task ReadPacketsAsync_H264AacFixture_Should_Equal_The_Synchronous_Read()
        {
            await using var source = Mp4FileSource.Open(H264AacFixture);
            var expected = source.ReadPackets().ToList();

            var packets = new List<MediaPacket>();
            await foreach (var packet in source.ReadPacketsAsync())
            {
                packets.Add(packet);
            }

            packets.Select(p => (p.TrackId, p.Pts, p.Dts, p.IsKeyframe)).Should().Equal(expected.Select(p => (p.TrackId, p.Pts, p.Dts, p.IsKeyframe)));
            packets.Zip(expected).All(pair => pair.First.Payload.Span.SequenceEqual(pair.Second.Payload.Span)).Should().BeTrue();
        }

        [Fact]
        public void ReadPackets_WithStartTime_Should_Start_Video_At_The_Keyframe_And_Audio_One_Packet_Early()
        {
            using var source = Mp4FileSource.Open(H264AacFixture);

            var packets = source.ReadPackets(TimeSpan.FromSeconds(0.25)).ToList();

            var video = packets.Where(p => p.TrackId == 1).ToList();
            video[0].Pts.Should().Be(2560);
            video[0].IsKeyframe.Should().BeTrue();
            video.Should().HaveCount(5);
            packets.First(p => p.TrackId == 2).Pts.Should().Be(9216);
        }

        [Fact]
        public void ReadPackets_StartBeforeFirstKeyframe_Should_Start_At_The_First_Sample()
        {
            using var source = Mp4FileSource.Open(H264AacFixture);

            var packets = source.ReadPackets(TimeSpan.FromMilliseconds(1)).ToList();

            packets.Count(p => p.TrackId == 1).Should().Be(10);
            packets.Count(p => p.TrackId == 2).Should().Be(19);
        }

        [Fact]
        public async Task ReadPacketsAsync_Cancelled_Should_Throw()
        {
            await using var source = Mp4FileSource.Open(H264AacFixture);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = async () =>
            {
                await foreach (var _ in source.ReadPacketsAsync(cancellationToken: cancellation.Token))
                {
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void ReadPackets_HevcFixture_Should_Match_Ffprobe()
        {
            using var source = Mp4FileSource.Open(HevcFixture);

            var track = source.Tracks.Tracks.Should().ContainSingle().Subject;
            track.Codec.Should().Be(MediaCodec.H265);
            track.Width.Should().Be(64);
            track.Height.Should().Be(48);
            track.Rotation.Should().Be(0);
            track.Timescale.Should().Be(12288);

            source.ReadPackets().Select(p => (p.Pts, p.Dts, p.Payload.Length, p.IsKeyframe))
                .Should().Equal((0L, 0L, 1640, true), (512L, 512L, 48, false), (1024L, 1024L, 38, false));
        }

        [Fact]
        public void Tracks_Vp9Track_Should_Be_Skipped_And_Aac_Kept()
        {
            using var source = Mp4FileSource.Open(Vp9AacFixture);

            source.SkippedTrackCount.Should().Be(1);
            var audio = source.Tracks.Tracks.Should().ContainSingle().Subject;
            audio.TrackId.Should().Be(2);
            audio.Channels.Should().Be(1);
            audio.SampleRate.Should().Be(8000);
            source.ReadPackets().Should().OnlyContain(p => p.TrackId == 2);
        }

        [Fact]
        public void ReadPackets_VideoOnlyFixture_Should_Yield_Every_Sample()
        {
            using var source = Mp4FileSource.Open(Path.GetFullPath("Codecs/Mov/test.mp4"));

            var packets = source.ReadPackets().ToList();

            packets.Should().HaveCount(125);
            packets[0].Payload.Length.Should().Be(6162);
            packets.Count(p => p.IsKeyframe).Should().Be(1);
        }

        [Fact]
        public void Constructor_FragmentedMp4_Should_Throw_NotSupported()
        {
            var bytes = Box("moov", Box("mvex", Box("trex", new byte[24])));

            var act = () => new Mp4FileSource(new MemoryStream(bytes));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Constructor_NonSeekableStream_Should_Throw()
        {
            var act = () => new Mp4FileSource(new NonSeekableStream(File.ReadAllBytes(HevcFixture)));

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_WithoutMoov_Should_Throw_InvalidData()
        {
            var act = () => new Mp4FileSource(new MemoryStream(Box("free", new byte[8])));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Constructor_TruncatedMoov_Should_Throw_InvalidData()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            var moov = IndexOf(bytes, "moov") - 4;

            var act = () => new Mp4FileSource(new MemoryStream(bytes[..(moov + 120)]));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void ReadPackets_SampleOffsetPastEndOfFile_Should_Throw_InvalidData()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(IndexOf(bytes, "stco") + 12), 0x7FFFFFF0);
            using var source = new Mp4FileSource(new MemoryStream(bytes));

            var act = () => source.ReadPackets().ToList();

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void ReadPackets_AfterDispose_Should_Throw()
        {
            var stream = new MemoryStream(File.ReadAllBytes(HevcFixture));
            var source = new Mp4FileSource(stream, leaveOpen: true);
            source.Dispose();
            source.Dispose();

            var act = () => source.ReadPackets();

            act.Should().Throw<ObjectDisposedException>();
            stream.CanRead.Should().BeTrue();
        }

        [Fact]
        public void GetSampleCount_UnknownTrack_Should_Throw()
        {
            using var source = Mp4FileSource.Open(HevcFixture);

            var act = () => source.GetSampleCount(7);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Open_MissingMoov_Should_Throw_And_Close_The_File()
        {
            var path = Path.Combine(Path.GetTempPath(), $"mp4_source_bad_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(path, Box("free", new byte[8]));
            try
            {
                var act = () => Mp4FileSource.Open(path);

                act.Should().Throw<InvalidDataException>();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void WidenNalLengths_TwoByteLengths_Should_Become_Four_Byte_Lengths()
        {
            var widened = Mp4FileSource.WidenNalLengths([0, 2, 0x65, 0x88, 0, 1, 0x06], 2);

            widened.Should().Equal(0, 0, 0, 2, 0x65, 0x88, 0, 0, 0, 1, 0x06);
        }

        [Theory]
        [InlineData(new byte[] { 0 }, 2)]
        [InlineData(new byte[] { 0, 5, 1 }, 2)]
        public void WidenNalLengths_Truncated_Should_Throw_InvalidData(byte[] payload, int lengthSize)
        {
            var act = () => Mp4FileSource.WidenNalLengths(payload, lengthSize);

            act.Should().Throw<InvalidDataException>();
        }

        [Theory]
        [InlineData(0x10000, 0, 0)]
        [InlineData(0, 0x10000, 90)]
        [InlineData(-0x10000, 0, 180)]
        [InlineData(0, -0x10000, 270)]
        [InlineData(0, 0, 0)]
        public void RotationFromMatrix_Should_Map_Quarter_Turns(int a, int b, int expected)
        {
            Mp4TrackReader.RotationFromMatrix(a, b).Should().Be(expected);
        }

        [Fact]
        public void ReadEdit_EmptyEditThenMediaEdit_Should_Delay_By_The_Empty_Edit()
        {
            var elst = Concat(new byte[4], U32(2), U32(500), U32(uint.MaxValue), U32(0x10000), U32(400), U32(256), U32(0x10000));
            var trak = Box("trak", Box("edts", Box("elst", elst)));

            var edit = Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1000, 12800);

            edit.Shift.Should().Be(6400 - 256);
            edit.End.Should().Be(6400 + 5120);
        }

        [Fact]
        public void ReadEdit_TwoMediaEdits_Should_Leave_The_End_Open()
        {
            var elst = Concat(new byte[4], U32(2), U32(400), U32(256), U32(0x10000), U32(400), U32(0), U32(0x10000));
            var trak = Box("trak", Box("edts", Box("elst", elst)));

            var edit = Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1000, 12800);

            edit.Shift.Should().Be(-256);
            edit.End.Should().BeNull();
        }

        [Fact]
        public void ReadEdit_ZeroMovieTimescale_Should_Leave_The_End_Open()
        {
            var elst = Concat(new byte[4], U32(1), U32(400), U32(1024), U32(0x10000));
            var trak = Box("trak", Box("edts", Box("elst", elst)));

            var edit = Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 0, 48000);

            edit.Shift.Should().Be(-1024);
            edit.End.Should().BeNull();
        }

        [Fact]
        public void ReadEdit_Version1_OnlyEmptyEdit_Should_Return_The_Delay()
        {
            var elst = Concat([1, 0, 0, 0], U32(1), U64(1000), U64(ulong.MaxValue), U32(0x10000));
            var trak = Box("trak", Box("edts", Box("elst", elst)));

            var edit = Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1000, 44100);

            edit.Should().Be(new Mp4Edit(44100, null));
        }

        [Fact]
        public void ReadEdit_EntryCountPastBox_Should_Throw_InvalidData()
        {
            var trak = Box("trak", Box("edts", Box("elst", Concat(new byte[4], U32(5)))));

            var act = () => Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1000, 1000);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void ReadEdit_HugeEmptyEdit_Should_Throw_InvalidData()
        {
            var elst = Concat([1, 0, 0, 0], U32(1), U64(long.MaxValue), U64(ulong.MaxValue), U32(0x10000));
            var trak = Box("trak", Box("edts", Box("elst", elst)));

            var act = () => Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1, 90000);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Constructor_UniformSampleSizeLargerThanTheFile_Should_Throw_InvalidData()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            var stsz = IndexOf(bytes, "stsz");
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(stsz + 8), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(stsz + 12), 0x1FFFFFFF);

            var act = () => new Mp4FileSource(new MemoryStream(bytes));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Constructor_SampleSizeAboveIntMax_Should_Throw_InvalidData()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(IndexOf(bytes, "stsz") + 16), 0xFFFFFFFF);

            var act = () => new Mp4FileSource(new MemoryStream(bytes));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Constructor_AtomSmallerThanItsHeader_Should_Throw_InvalidData()
        {
            var bytes = Concat(U32(4), Encoding.ASCII.GetBytes("moov"), new byte[8]);

            var act = () => new Mp4FileSource(new MemoryStream(bytes));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void MovProbe_VideoTrackWithoutStts_Should_Leave_Frame_Fields_Empty()
        {
            var bytes = File.ReadAllBytes(HevcFixture);
            var stts = IndexOf(bytes, "stts");
            bytes[stts] = (byte)'x';

            var result = MovProbe.Probe(new MemoryStream(bytes));

            result.FrameCount.Should().BeNull();
            result.FrameRate.Should().BeNull();
        }

        [Fact]
        public void ReadEdit_WithoutEditList_Should_Be_Zero()
        {
            var trak = Box("trak", Box("tkhd", new byte[84]));

            Mp4TrackReader.ReadEdit(new MemoryStream(trak), new MovAtom("trak", 8, trak.Length), 1000, 1000).Should().Be(new Mp4Edit(0, null));
        }

        [Fact]
        public void ReadTiming_SttsShorterThanSampleTable_Should_Throw_InvalidData()
        {
            var stbl = Box("stbl", Box("stts", Concat(new byte[4], U32(1), U32(2), U32(512))));

            var act = () => Mp4TrackReader.ReadTiming(new MemoryStream(stbl), new MovAtom("stbl", 8, stbl.Length), 3);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void ReadTiming_NegativeCompositionOffsets_Should_Be_Read_Signed()
        {
            var stbl = Box("stbl",
                Box("stts", Concat(new byte[4], U32(1), U32(3), U32(512))),
                Box("ctts", Concat([1, 0, 0, 0], U32(2), U32(1), U32(unchecked((uint)-512)), U32(2), U32(512))));

            var timing = Mp4TrackReader.ReadTiming(new MemoryStream(stbl), new MovAtom("stbl", 8, stbl.Length), 3);

            timing.DecodeTimes.Should().Equal(0, 512, 1024);
            timing.CompositionOffsets.Should().Equal(-512, 512, 512);
            timing.TotalDuration.Should().Be(1536);
        }

        [Fact]
        public void MovProbe_H264AacFixture_Should_Report_Audio_Frames_Rate_And_Rotation()
        {
            var result = MovProbe.Probe(H264AacFixture);

            result.HasAudio.Should().BeTrue();
            result.FrameCount.Should().Be(10);
            result.FrameRate.Should().Be(25);
            result.Rotation.Should().Be(270);
        }

        [Fact]
        public void MovProbe_VideoOnlyFixture_Should_Report_No_Audio()
        {
            var result = MovProbe.Probe(Path.GetFullPath("Codecs/Mov/test.mp4"));

            result.HasAudio.Should().BeFalse();
            result.FrameCount.Should().Be(125);
            result.Rotation.Should().Be(0);
        }

        [Fact]
        public async Task NativeEncoderProbe_H264AacFixture_Should_Carry_The_Video_Fields()
        {
            var encoder = new NativeEncoder(Microsoft.Extensions.Logging.Abstractions.NullLogger<NativeEncoder>.Instance, enableLogging: false);

            var result = await encoder.Probe(H264AacFixture);

            result.FrameCount.Should().Be(10);
            result.FrameRate.Should().Be(25);
            result.Rotation.Should().Be(270);
            result.HasAudio.Should().BeTrue();
        }

        [Fact]
        public void MediaTrackInfo_Rotation_Should_Accept_Only_Quarter_Turns()
        {
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, Array.Empty<byte>(), 0);

            (track with { Rotation = 270 }).Rotation.Should().Be(270);
            var act = () => track with { Rotation = 45 };
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void ReadDecoderConfig_Should_Return_The_Object_Type_And_AudioSpecificConfig()
        {
            var esds = Mp4EsdsParser.BuildEsdsBoxContent([0x12, 0x10]);

            var config = Mp4EsdsParser.ReadDecoderConfig(esds);

            config.ObjectTypeIndication.Should().Be(0x40);
            config.DecoderSpecificInfo.Should().Equal(0x12, 0x10);
        }

        [Fact]
        public void ReadDecoderConfig_WithoutEsDescriptor_Should_Throw_InvalidData()
        {
            var act = () => Mp4EsdsParser.ReadDecoderConfig([0, 0, 0, 0, 0x04, 0x00]);

            act.Should().Throw<InvalidDataException>();
        }

        private static int IndexOf(byte[] bytes, string fourCc) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(fourCc));

        private static byte[] Box(string fourCc, params byte[][] parts)
        {
            var content = Concat(parts);
            return Concat(U32((uint)(content.Length + 8)), Encoding.ASCII.GetBytes(fourCc), content);
        }

        private static byte[] U32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] U64(ulong value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    }
}
