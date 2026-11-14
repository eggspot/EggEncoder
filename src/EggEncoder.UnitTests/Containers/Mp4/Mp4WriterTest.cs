using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Mp4
{
    public class Mp4WriterTest
    {
        public static readonly string RemuxFixture = Path.GetFullPath("Containers/Mp4/writer_remux.mp4");

        [Fact]
        public void Write_RemuxedFixture_Should_Read_Back_Identical_Packets_And_Tracks()
        {
            var (tracks, packets) = ReadAll(Mp4FileSourceTest.H264AacFixture);

            var bytes = WriteAll(tracks, packets);

            var (readTracks, readPackets) = ReadAll(bytes);
            readTracks.Tracks.Select(t => (t.TrackId, t.Kind, t.Codec, t.Timescale, t.Width, t.Height, t.Rotation, t.SampleRate, t.Channels))
                .Should().Equal(tracks.Tracks.Select(t => (t.TrackId, t.Kind, t.Codec, t.Timescale, t.Width, t.Height, t.Rotation, t.SampleRate, t.Channels)));
            readTracks.Tracks.Zip(tracks.Tracks).All(p => p.First.CodecConfiguration.Span.SequenceEqual(p.Second.CodecConfiguration.Span)).Should().BeTrue();
            Describe(readPackets.Where(p => p.TrackId == 1)).Should().Equal(Describe(packets.Where(p => p.TrackId == 1)));
            Describe(readPackets.Where(p => p.TrackId == 2)).Should().Equal(Describe(packets.Where(p => p.TrackId == 2)));
            TopLevelBoxes(bytes).Should().Equal("ftyp", "moov", "mdat");
        }

        [Fact]
        public void Write_RemuxedFixture_Should_Match_The_Checked_In_File_Ffprobe_Read()
        {
            var (tracks, packets) = ReadAll(Mp4FileSourceTest.H264AacFixture);

            var bytes = WriteAll(tracks, packets);

            bytes.Should().Equal(File.ReadAllBytes(RemuxFixture));
            var probe = MovProbe.Probe(new MemoryStream(bytes));
            probe.FrameCount.Should().Be(10);
            probe.FrameRate.Should().Be(25);
            probe.Rotation.Should().Be(270);
            probe.HasAudio.Should().BeTrue();
            probe.DurationSeconds.Should().BeApproximately(0.418, 0.001);
        }

        [Fact]
        public async Task WriteAsync_HevcFixture_Should_Round_Trip()
        {
            var (tracks, packets) = ReadAll(Mp4FileSourceTest.HevcFixture);
            var destination = new MemoryStream();
            await using (var writer = new Mp4Writer(destination, leaveOpen: true, new MemoryStream()))
            {
                await writer.OpenAsync(tracks);
                foreach (var packet in packets)
                {
                    await writer.WriteAsync(packet);
                }

                await writer.CompleteAsync();
            }

            var (_, readPackets) = ReadAll(destination.ToArray());
            Describe(readPackets).Should().Equal(Describe(packets));
        }

        [Fact]
        public void Write_LateStartAndNegativeCompositionOffsets_Should_Read_Back_The_Same_Times()
        {
            var tracks = VideoTrackSet();
            var packets = new[]
            {
                new MediaPacket(1, 1024, 512, true, 0, NalUnit(1)),
                new MediaPacket(1, 1536, 1024, false, 0, NalUnit(2)),
                new MediaPacket(1, 1280, 1536, false, 0, NalUnit(3))
            };

            var bytes = WriteAll(tracks, packets);

            var (_, readPackets) = ReadAll(bytes);
            readPackets.Select(p => (p.Pts, p.Dts)).Should().Equal((1024L, 512L), (1536L, 1024L), (1280L, 1536L));
            bytes.AsSpan().IndexOf("ctts"u8).Should().BeGreaterThan(0);
            bytes[bytes.AsSpan().IndexOf("ctts"u8) + 4].Should().Be(1);
        }

        [Fact]
        public void Write_DurationsPast32Bits_Should_Use_Version1_Boxes()
        {
            var tracks = new MediaTrackSet([new MediaTrackInfo(1, MediaKind.Video, MediaCodec.H264, 1, Mp4FileSourceTest_AvcC(), 0)]);
            var packets = new[]
            {
                new MediaPacket(1, 0, 0, true, 0, NalUnit(1)),
                new MediaPacket(1, 4_000_000_000, 4_000_000_000, true, 0, NalUnit(2))
            };

            var bytes = WriteAll(tracks, packets);

            bytes[bytes.AsSpan().IndexOf("mvhd"u8) + 4].Should().Be(1);
            bytes[bytes.AsSpan().IndexOf("mdhd"u8) + 4].Should().Be(1);
            using var source = new Mp4FileSource(new MemoryStream(bytes));
            source.Duration.Should().Be(TimeSpan.FromSeconds(8_000_000_000));
            Mp4WriterTest_SampleDurations(bytes).Should().Equal(4_000_000_000L, 4_000_000_000L);
            source.ReadPackets().Select(p => p.Dts).Should().Equal(0, 4_000_000_000);
        }

        [Fact]
        public void Write_TwoSecondsOfAudio_Should_Use_Chunks_Of_At_Most_Half_A_Second()
        {
            var tracks = new MediaTrackSet([new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Aac, 44100, new byte[] { 0x12, 0x10 }, 0, 44100, 2)]);
            var packets = Enumerable.Range(0, 87).Select(i => new MediaPacket(1, i * 1024L, i * 1024L, true, 0, new byte[] { (byte)i })).ToArray();

            var bytes = WriteAll(tracks, packets);

            var stco = bytes.AsSpan().IndexOf("stco"u8);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stco + 8)).Should().Be(4);
            ReadAll(bytes).Packets.Select(p => p.Payload.Span[0]).Should().Equal(Enumerable.Range(0, 87).Select(i => (byte)i));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(90)]
        [InlineData(180)]
        [InlineData(270)]
        public void WriteRotationMatrix_Should_Read_Back_The_Same_Rotation(int rotation)
        {
            var builder = new Mp4BoxBuilder();

            Mp4MoovBuilder.WriteRotationMatrix(builder, rotation, 64, 48);

            var matrix = builder.ToArray();
            Mp4TrackReader.RotationFromMatrix(BinaryPrimitives.ReadInt32BigEndian(matrix), BinaryPrimitives.ReadInt32BigEndian(matrix.AsSpan(4))).Should().Be(rotation);
        }

        [Fact]
        public void Write_EmptyTrack_Should_Produce_A_Readable_File()
        {
            var bytes = WriteAll(VideoTrackSet(), []);

            using var source = new Mp4FileSource(new MemoryStream(bytes));
            source.ReadPackets().Should().BeEmpty();
            source.GetSampleCount(1).Should().Be(0);
        }

        [Fact]
        public void Write_Should_Reject_Bad_Packets()
        {
            using var writer = new Mp4Writer(new MemoryStream(), scratch: new MemoryStream());
            writer.Open(VideoTrackSet());
            writer.Write(new MediaPacket(1, 0, 0, true, 0, NalUnit(1)));

            ((Action)(() => writer.Write(new MediaPacket(9, 1, 1, true, 0, NalUnit(1))))).Should().Throw<ArgumentException>();
            ((Action)(() => writer.Write(new MediaPacket(1, 1, 1, true, 1, NalUnit(1))))).Should().Throw<ArgumentException>();
            ((Action)(() => writer.Write(new MediaPacket(1, 0, 0, true, 0, NalUnit(1))))).Should().Throw<ArgumentException>();
            ((Action)(() => writer.Write(new MediaPacket(1, 1L << 40, 1, true, 0, NalUnit(1))))).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Lifecycle_Misuse_Should_Throw()
        {
            var writer = new Mp4Writer(new MemoryStream(), scratch: new MemoryStream());
            ((Action)(() => writer.Write(new MediaPacket(1, 0, 0, true, 0, NalUnit(1))))).Should().Throw<InvalidOperationException>();
            ((Action)(() => writer.Open(new MediaTrackSet([])))).Should().Throw<ArgumentException>();
            writer.Open(VideoTrackSet());
            ((Action)(() => writer.Open(VideoTrackSet()))).Should().Throw<InvalidOperationException>();
            writer.Complete();
            ((Action)(() => writer.Complete())).Should().Throw<InvalidOperationException>();
            writer.Dispose();
            writer.Dispose();
            ((Action)(() => writer.Complete())).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void Open_Mp3Track_Should_Throw_NotSupported()
        {
            using var writer = new Mp4Writer(new MemoryStream(), scratch: new MemoryStream());

            var act = () => writer.Open(new MediaTrackSet([new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Mp3, 44100, Array.Empty<byte>(), 0, 44100, 2)]));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Constructor_Should_Validate_Streams_And_Leave_A_Caller_Scratch_Open()
        {
            var scratch = new MemoryStream();
            ((Action)(() => new Mp4Writer(new MemoryStream([], writable: false)))).Should().Throw<ArgumentException>();
            ((Action)(() => new Mp4Writer(new MemoryStream(), scratch: new MemoryStream([], writable: false)))).Should().Throw<ArgumentException>();

            new Mp4Writer(new MemoryStream(), scratch: scratch).Dispose();

            scratch.CanRead.Should().BeTrue();
        }

        [Fact]
        public void Join_FixtureTwice_Should_Continue_Timestamps_And_Drop_The_Second_Priming_Packet()
        {
            var input = File.ReadAllBytes(Mp4FileSourceTest.H264AacFixture);
            var destination = new MemoryStream();

            Mp4Joiner.Join([new MemoryStream(input), new MemoryStream(input)], destination, new MemoryStream());

            var (tracks, packets) = ReadAll(destination.ToArray());
            tracks.Tracks.Should().HaveCount(2);
            var video = packets.Where(p => p.TrackId == 1).ToList();
            var audio = packets.Where(p => p.TrackId == 2).ToList();
            video.Should().HaveCount(20);
            audio.Should().HaveCount(37);
            video[10].Pts.Should().Be(5120);
            video[10].Dts.Should().Be(4096);
            video[10].IsKeyframe.Should().BeTrue();
            audio[19].Pts.Should().Be(17640);
            video.Select(p => p.Dts).Should().BeInAscendingOrder();
            audio.Select(p => p.Dts).Should().BeInAscendingOrder();
        }

        [Fact]
        public void Join_Files_On_Disk_Should_Write_The_Destination()
        {
            var destination = Path.Combine(Path.GetTempPath(), $"mp4_join_{Guid.NewGuid():N}.mp4");
            try
            {
                Mp4Joiner.Join([Mp4FileSourceTest.HevcFixture, Mp4FileSourceTest.HevcFixture], destination);

                using var source = Mp4FileSource.Open(destination);
                source.ReadPackets().Select(p => p.Pts).Should().Equal(0, 512, 1024, 1536, 2048, 2560);
            }
            finally
            {
                File.Delete(destination);
            }
        }

        [Fact]
        public void Join_DifferentTracks_Should_Throw()
        {
            var act = () => Mp4Joiner.Join([File.OpenRead(Mp4FileSourceTest.H264AacFixture), File.OpenRead(Mp4FileSourceTest.HevcFixture)], new MemoryStream(), new MemoryStream());
            var sameCountDifferentCodec = () => Mp4Joiner.Join([File.OpenRead(Mp4FileSourceTest.HevcFixture), File.OpenRead(Path.GetFullPath("Codecs/Mov/test.mp4"))], new MemoryStream(), new MemoryStream());
            var none = () => Mp4Joiner.Join(Array.Empty<Stream>(), new MemoryStream());

            act.Should().Throw<ArgumentException>();
            sameCountDifferentCodec.Should().Throw<ArgumentException>();
            none.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Split_AtTheSecondKeyframe_Should_Reset_Each_Parts_Timestamps()
        {
            var parts = new List<MemoryStream>();

            var starts = Mp4Splitter.Split(File.OpenRead(Mp4FileSourceTest.H264AacFixture), [TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.15)], _ =>
            {
                var part = new MemoryStream();
                parts.Add(part);
                return part;
            });

            starts.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(0.2));
            parts.Should().HaveCount(2);
            var (_, first) = ReadAll(parts[0].ToArray());
            var (_, second) = ReadAll(parts[1].ToArray());
            first.Count(p => p.TrackId == 1).Should().Be(5);
            first.Count(p => p.TrackId == 2).Should().Be(10);
            var secondVideo = second.Where(p => p.TrackId == 1).ToList();
            secondVideo.Should().HaveCount(5);
            secondVideo[0].Pts.Should().Be(0);
            secondVideo[0].Dts.Should().Be(-1024);
            secondVideo[0].IsKeyframe.Should().BeTrue();
            second.Where(p => p.TrackId == 2).Select(p => p.Pts).First().Should().Be(396);
            second.Count(p => p.TrackId == 2).Should().Be(9);
        }

        [Fact]
        public void Split_PastTheLastKeyframe_Should_Write_One_Part()
        {
            var parts = 0;

            var starts = Mp4Splitter.Split(File.OpenRead(Mp4FileSourceTest.H264AacFixture), [TimeSpan.FromSeconds(0.35)], _ =>
            {
                parts++;
                return new MemoryStream();
            });

            starts.Should().Equal(TimeSpan.Zero);
            parts.Should().Be(1);
        }

        [Fact]
        public void Split_AudioOnly_Should_Cut_At_The_First_Packet_After_The_Time()
        {
            var parts = new List<MemoryStream>();

            var starts = Mp4Splitter.Split(File.OpenRead(Mp4FileSourceTest.Vp9AacFixture), [TimeSpan.FromSeconds(0.1)], _ =>
            {
                var part = new MemoryStream();
                parts.Add(part);
                return part;
            });

            starts.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(0.128));
            ReadAll(parts[1].ToArray()).Packets[0].Pts.Should().Be(0);
        }

        [Fact]
        public void Ffprobe_OnWrittenAndJoinedMp4_Should_Read_Every_Packet()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var input = File.ReadAllBytes(Mp4FileSourceTest.H264AacFixture);
            var joined = new MemoryStream();
            Mp4Joiner.Join([new MemoryStream(input), new MemoryStream(input)], joined, new MemoryStream());
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-mp4-{Guid.NewGuid():N}.mp4");
            try
            {
                File.WriteAllBytes(path, joined.ToArray());
                var output = Run("ffprobe", $"-v error -show_entries packet=stream_index,pts,dts -of csv \"{path}\"");
                output.Split('\n').Count(l => l.StartsWith("packet,0,")).Should().Be(20);
                output.Split('\n').Count(l => l.StartsWith("packet,1,")).Should().Be(37);
                Run("ffmpeg", $"-v error -i \"{path}\" -f null -").Should().BeEmpty();
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static long[] Mp4WriterTest_SampleDurations(byte[] bytes)
        {
            using var source = new Mp4FileSource(new MemoryStream(bytes));
            return source.SourceTracks[0].Durations;
        }

        private static string Run(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0);
            return output.Trim();
        }

        internal static (MediaTrackSet Tracks, List<MediaPacket> Packets) ReadAll(string path) => ReadAll(File.ReadAllBytes(path));

        internal static (MediaTrackSet Tracks, List<MediaPacket> Packets) ReadAll(byte[] bytes)
        {
            using var source = new Mp4FileSource(new MemoryStream(bytes));
            return (source.Tracks, source.ReadPackets().ToList());
        }

        internal static byte[] WriteAll(MediaTrackSet tracks, IEnumerable<MediaPacket> packets)
        {
            var destination = new MemoryStream();
            using (var writer = new Mp4Writer(destination, leaveOpen: true, new MemoryStream()))
            {
                writer.Open(tracks);
                foreach (var packet in packets)
                {
                    writer.Write(packet);
                }

                writer.Complete();
            }

            return destination.ToArray();
        }

        private static IEnumerable<(long Pts, long Dts, bool Key, string Payload)> Describe(IEnumerable<MediaPacket> packets) =>
            packets.Select(p => (p.Pts, p.Dts, p.IsKeyframe, Convert.ToHexString(p.Payload.Span)));

        private static List<string> TopLevelBoxes(byte[] bytes)
        {
            var boxes = new List<string>();
            for (var position = 0; position + 8 <= bytes.Length;)
            {
                boxes.Add(Encoding.ASCII.GetString(bytes, position + 4, 4));
                position += (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            }

            return boxes;
        }

        private static byte[] Mp4FileSourceTest_AvcC()
        {
            using var source = Mp4FileSource.Open(Mp4FileSourceTest.H264AacFixture);
            return source.Tracks.Tracks[0].CodecConfiguration.ToArray();
        }

        private static MediaTrackSet VideoTrackSet() =>
            new([new MediaTrackInfo(1, MediaKind.Video, MediaCodec.H264, 12800, Mp4FileSourceTest_AvcC(), 0, width: 64, height: 48)]);

        private static byte[] NalUnit(byte value) => [0, 0, 0, 2, 0x65, value];
    }
}
