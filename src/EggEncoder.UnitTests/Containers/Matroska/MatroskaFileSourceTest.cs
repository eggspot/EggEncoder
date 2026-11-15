using EggEncoder.Containers.Matroska;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Matroska
{
    public class MatroskaFileSourceTest
    {
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Containers", "Matroska", name);

        [Fact]
        public void H264_Aac_Mkv_Should_Match_Ffprobe()
        {
            using var source = MatroskaFileSource.Open(Fixture("h264_aac.mkv"));
            var packets = source.ReadPackets().ToList();

            source.DocType.Should().Be("matroska");
            source.SkippedTrackCount.Should().Be(0);
            source.Duration.TotalMilliseconds.Should().BeApproximately(423, 1);
            var video = source.Tracks.FindTrack(1)!;
            video.Codec.Should().Be(MediaCodec.H264);
            (video.Width, video.Height, video.Timescale).Should().Be((64, 48, 1000));
            var audio = source.Tracks.FindTrack(2)!;
            audio.Codec.Should().Be(MediaCodec.Aac);
            (audio.SampleRate, audio.Channels).Should().Be((44100, 2));

            var videoPackets = packets.Where(p => p.TrackId == 1).ToList();
            videoPackets.Select(p => p.Pts).Should().Equal(0, 120, 63, 103, 183, 223, 343, 263, 303, 383);
            videoPackets.Select(p => p.Dts).Should().Equal(-40, 23, 63, 80, 143, 183, 223, 263, 303, 343);
            videoPackets.Count(p => p.IsKeyframe).Should().Be(2);
            var audioPackets = packets.Where(p => p.TrackId == 2).ToList();
            audioPackets.Should().HaveCount(19);
            audioPackets.Take(4).Select(p => p.Pts).Should().Equal(0, 23, 46, 69);
            audioPackets.Should().OnlyContain(p => p.IsKeyframe && p.Dts == p.Pts);
        }

        [Fact]
        public void H264_Payloads_Should_Equal_The_Mp4_They_Were_Remuxed_From()
        {
            using var mkv = MatroskaFileSource.Open(Fixture("h264_aac.mkv"));
            using var mp4 = Mp4FileSource.Open(Path.Combine(AppContext.BaseDirectory, "Containers", "Mp4", "h264_aac_rotated.mp4"));
            var mp4Video = mp4.Tracks.Tracks.First(t => t.Kind == MediaKind.Video).TrackId;

            var fromMkv = mkv.ReadPackets().Where(p => p.TrackId == 1).Select(p => Convert.ToHexString(p.Payload.Span)).ToList();
            var fromMp4 = mp4.ReadPackets().Where(p => p.TrackId == mp4Video).Select(p => Convert.ToHexString(p.Payload.Span)).ToList();

            fromMkv.Should().Equal(fromMp4);
        }

        [Fact]
        public void Vp9_Opus_Webm_Should_Match_Ffprobe()
        {
            using var source = MatroskaFileSource.Open(Fixture("vp9_opus.webm"));
            var packets = source.ReadPackets().ToList();

            source.DocType.Should().Be("webm");
            source.Tracks.FindTrack(1)!.Codec.Should().Be(MediaCodec.Vp9);
            var opus = source.Tracks.FindTrack(2)!;
            opus.Codec.Should().Be(MediaCodec.Opus);
            System.Text.Encoding.ASCII.GetString(opus.CodecConfiguration.Span[..8]).Should().Be("OpusHead");
            var video = packets.Where(p => p.TrackId == 1).ToList();
            video.Select(p => p.Pts).Should().Equal(7, 107, 207, 307, 407, 507, 607, 707, 807, 907);
            video.Count(p => p.IsKeyframe).Should().Be(1);
            var audio = packets.Where(p => p.TrackId == 2).ToList();
            audio.Should().HaveCount(51);
            audio.Take(3).Select(p => p.Pts).Should().Equal(-7, 14, 34);
        }

        [Fact]
        public void A_Non_Seekable_Stream_Should_Give_The_Same_Packets()
        {
            var bytes = File.ReadAllBytes(Fixture("h264_aac.mkv"));
            using var seekable = new MatroskaFileSource(new MemoryStream(bytes));
            using var forwardOnly = new MatroskaFileSource(new ForwardOnlyStream(bytes));

            var expected = seekable.ReadPackets().Select(Describe).ToList();

            forwardOnly.ReadPackets().Select(Describe).Should().Equal(expected);
        }

        [Fact]
        public void Unknown_Size_Clusters_And_Every_Lacing_Should_Be_Read()
        {
            var track = Ebml.Element(0xAE, Ebml.Uint(0xD7, 1), Ebml.Uint(0x83, 2), Ebml.Text(0x86, "A_MPEG/L3"), Ebml.Uint(0x23E383, 10_000_000),
                Ebml.Element(0xE1, Ebml.Float(0xB5, 44100), Ebml.Uint(0x9F, 2)));
            var xiph = Ebml.Block(0xA3, 1, 0, 0x80 | 0x02, [3, 1], [[1, 1, 1], [2], [3, 3]]);
            var ebmlLaced = Ebml.Block(0xA3, 1, 30, 0x80 | 0x06, [0x82, 0xBF], [[4, 4], [5, 5], [6]]);
            var fixedLaced = Ebml.Block(0xA3, 1, 60, 0x80 | 0x04, [], [[7, 7], [8, 8]]);
            var file = Ebml.File("matroska",
                Ebml.Element(0x1549A966, Ebml.Uint(0x2AD7B1, 1_000_000)),
                Ebml.Element(0x1654AE6B, track),
                Ebml.UnknownSize(0x1F43B675, Ebml.Uint(0xE7, 1000), xiph),
                Ebml.UnknownSize(0x1F43B675, Ebml.Uint(0xE7, 2000), ebmlLaced, fixedLaced));

            using var source = new MatroskaFileSource(new MemoryStream(file));
            var packets = source.ReadPackets().ToList();

            packets.Select(p => p.Pts).Should().Equal(1000, 1010, 1020, 2030, 2040, 2050, 2060, 2070);
            packets.Select(p => Convert.ToHexString(p.Payload.Span)).Should().Equal("010101", "02", "0303", "0404", "0505", "06", "0707", "0808");
        }

        [Fact]
        public void Unsupported_And_Encoded_Tracks_Should_Be_Skipped()
        {
            var subtitles = Ebml.Element(0xAE, Ebml.Uint(0xD7, 1), Ebml.Uint(0x83, 0x11), Ebml.Text(0x86, "S_TEXT/UTF8"));
            var compressed = Ebml.Element(0xAE, Ebml.Uint(0xD7, 2), Ebml.Uint(0x83, 2), Ebml.Text(0x86, "A_MPEG/L3"), Ebml.Element(0x6D80));
            var mp3 = Ebml.Element(0xAE, Ebml.Uint(0xD7, 3), Ebml.Uint(0x83, 2), Ebml.Text(0x86, "A_MPEG/L3"));
            var file = Ebml.File("webm", Ebml.Element(0x1654AE6B, subtitles, compressed, mp3), Ebml.Element(0x1F43B675, Ebml.Uint(0xE7, 0),
                Ebml.Block(0xA3, 1, 0, 0x80, [], [[9]]), Ebml.Block(0xA3, 3, 0, 0x80, [], [[10]])));

            using var source = new MatroskaFileSource(new MemoryStream(file));

            source.SkippedTrackCount.Should().Be(2);
            source.Tracks.Tracks.Select(t => t.TrackId).Should().Equal(3);
            source.ReadPackets().Select(p => p.TrackId).Should().Equal(3);
        }

        [Fact]
        public void Not_Matroska_Should_Throw_InvalidData()
        {
            var act = () => new MatroskaFileSource(new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Containers", "Mp4", "hevc.mp4"))));

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Damaged_Files_Should_Only_Throw_InvalidData()
        {
            var original = File.ReadAllBytes(Fixture("h264_aac.mkv"));
            var random = new Random(11);
            for (var round = 0; round < 400; round++)
            {
                var bytes = (byte[])original.Clone();
                if (round % 4 == 0)
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

                try
                {
                    using var source = new MatroskaFileSource(new MemoryStream(bytes));
                    foreach (var packet in source.ReadPackets())
                    {
                        _ = packet.Payload.Length;
                    }
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        [Fact]
        public void Reading_Twice_Should_Throw()
        {
            using var source = MatroskaFileSource.Open(Fixture("vp9_opus.webm"));
            _ = source.ReadPackets().ToList();

            source.Invoking(s => s.ReadPackets()).Should().Throw<InvalidOperationException>();
        }

        private static string Describe(MediaPacket packet) => $"{packet.TrackId}:{packet.Pts}:{packet.Dts}:{packet.IsKeyframe}:{Convert.ToHexString(packet.Payload.Span)}";

        private sealed class ForwardOnlyStream(byte[] bytes) : Stream
        {
            private readonly MemoryStream _inner = new(bytes);

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
