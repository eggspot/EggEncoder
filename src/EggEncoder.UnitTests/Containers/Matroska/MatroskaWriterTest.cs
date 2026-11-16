using System.Diagnostics;
using EggEncoder.Containers.Matroska;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Matroska
{
    public class MatroskaWriterTest
    {
        private static string Fixture(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "Containers", folder, name);

        [Theory]
        [InlineData("h264_aac.mkv", MatroskaDocType.Matroska)]
        [InlineData("vp9_opus.webm", MatroskaDocType.WebM)]
        public void Rewriting_A_File_Should_Give_The_Same_Packets_Back(string fixture, MatroskaDocType docType)
        {
            using var original = MatroskaFileSource.Open(Fixture("Matroska", fixture));
            var packets = original.ReadPackets().ToList();

            var written = Write(original.Tracks, packets, docType, seekable: true);
            using var reread = new MatroskaFileSource(new MemoryStream(written));

            reread.DocType.Should().Be(docType == MatroskaDocType.WebM ? "webm" : "matroska");
            reread.Tracks.Tracks.Select(t => (t.Codec, t.Width, t.Height, t.SampleRate, t.Channels))
                .Should().Equal(original.Tracks.Tracks.Select(t => (t.Codec, t.Width, t.Height, t.SampleRate, t.Channels)));
            var rereadPackets = reread.ReadPackets().ToList();
            foreach (var track in original.Tracks.Tracks)
            {
                rereadPackets.Where(p => p.TrackId == track.TrackId).Select(Describe)
                    .Should().Equal(packets.Where(p => p.TrackId == track.TrackId).Select(Describe));
            }
            reread.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        }

        [Fact]
        public void An_Mp4_Remuxed_To_Matroska_Should_Keep_Its_Payloads_And_Times()
        {
            using var mp4 = Mp4FileSource.Open(Fixture("Mp4", "h264_aac_rotated.mp4"));
            var packets = mp4.ReadPackets().ToList();

            var written = Write(mp4.Tracks, packets, MatroskaDocType.Matroska, seekable: false);
            using var mkv = new MatroskaFileSource(new MemoryStream(written));
            var reread = mkv.ReadPackets().ToList();

            foreach (var track in mp4.Tracks.Tracks)
            {
                var number = mp4.Tracks.Tracks.ToList().IndexOf(track) + 1;
                var expected = packets.Where(p => p.TrackId == track.TrackId).ToList();
                var actual = reread.Where(p => p.TrackId == number).ToList();
                actual.Select(p => Convert.ToHexString(p.Payload.Span)).Should().Equal(expected.Select(p => Convert.ToHexString(p.Payload.Span)));
                actual.Select(p => p.Pts).Should().Equal(expected.Select(p => MediaTimestamp.Rescale(p.Pts, track.Timescale, 1000)));
            }

            mkv.Duration.Should().Be(TimeSpan.Zero);
        }

        [Fact]
        public void WebM_Should_Refuse_H264()
        {
            using var mkv = MatroskaFileSource.Open(Fixture("Matroska", "h264_aac.mkv"));
            using var writer = new MatroskaWriter(new MemoryStream(), MatroskaDocType.WebM);

            writer.Invoking(w => w.Open(mkv.Tracks)).Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Out_Of_Order_Or_Unknown_Packets_Should_Be_Refused()
        {
            using var mkv = MatroskaFileSource.Open(Fixture("Matroska", "h264_aac.mkv"));
            var packets = mkv.ReadPackets().Where(p => p.TrackId == 2).Take(2).ToList();
            using var writer = new MatroskaWriter(new MemoryStream());
            writer.Open(mkv.Tracks);
            writer.Write(packets[1]);

            writer.Invoking(w => w.Write(packets[0])).Should().Throw<ArgumentException>();
            writer.Invoking(w => w.Write(packets[1] with { TrackId = 9 })).Should().Throw<ArgumentException>();
            writer.Invoking(w => w.Write(packets[1] with { Dts = 1000, Pts = 1000, ConfigVersion = 3 })).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Ffprobe_Should_Read_The_Written_File()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            using var mp4 = Mp4FileSource.Open(Fixture("Mp4", "h264_aac_rotated.mp4"));
            var packets = mp4.ReadPackets().ToList();
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-{Guid.NewGuid():N}.mkv");
            try
            {
                File.WriteAllBytes(path, Write(mp4.Tracks, packets, MatroskaDocType.Matroska, seekable: true));
                var info = new ProcessStartInfo("ffprobe", $"-v error -show_entries stream=codec_name -show_entries format=nb_streams -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEnd();
                var errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                process.ExitCode.Should().Be(0, errors);
                errors.Should().BeEmpty();
                output.Should().Contain("h264").And.Contain("aac");
                var video = mp4.Tracks.Tracks.First(t => t.Kind == MediaKind.Video).TrackId;
                output.Should().Contain(packets.Count(p => p.TrackId == video).ToString());
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static byte[] Write(MediaTrackSet tracks, IEnumerable<MediaPacket> packets, MatroskaDocType docType, bool seekable)
        {
            Stream destination = seekable ? new MemoryStream() : new WriteOnlyStream();
            using (var writer = new MatroskaWriter(destination, docType, leaveOpen: true))
            {
                writer.Open(tracks);
                foreach (var packet in packets.OrderBy(p => MediaTimestamp.Rescale(p.Dts, tracks.FindTrack(p.TrackId)!.Timescale, 1_000_000)))
                {
                    writer.Write(packet);
                }

                writer.Complete();
            }

            return destination is WriteOnlyStream forward ? forward.ToArray() : ((MemoryStream)destination).ToArray();
        }

        private static string Describe(MediaPacket packet) => $"{packet.TrackId}:{packet.Pts}:{packet.Dts}:{packet.IsKeyframe}:{Convert.ToHexString(packet.Payload.Span)}";

        private sealed class WriteOnlyStream : Stream
        {
            private readonly MemoryStream _inner = new();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public byte[] ToArray() => _inner.ToArray();

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        }
    }
}
