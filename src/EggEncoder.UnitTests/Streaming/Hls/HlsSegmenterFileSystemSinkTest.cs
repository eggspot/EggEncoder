using System.Text;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Hls;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public sealed class HlsSegmenterFileSystemSinkTest : IDisposable
    {
        private const int SampleRate = 44100;
        private const int SamplesPerFrame = 1024;

        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("eggencoder-hls-sink-");

        public void Dispose() => _directory.Delete(recursive: true);

        [Fact]
        public void HlsSegmenter_MpegTs_WithFileSystemSink_Should_ListOnlyFilesThatExist()
        {
            var segmenter = new HlsSegmenter(1, SampleRate, 1, new FileSystemStreamingSink(_directory.FullName), SegmentFormat.MpegTs);

            AddSeconds(segmenter, 3);
            segmenter.Complete();

            var uris = PlaylistUris("playlist.m3u8");
            uris.Should().HaveCount(3).And.OnlyContain(uri => uri.EndsWith(".ts"));
            uris.Should().OnlyContain(uri => File.Exists(Path.Combine(_directory.FullName, uri)));
            Directory.GetFiles(_directory.FullName, "*.m4s").Should().BeEmpty();
        }

        [Fact]
        public void HlsSegmenter_Fmp4_WithFilePrefix_Should_PrefixEveryPlaylistUri()
        {
            var segmenter = new HlsSegmenter(1, SampleRate, 1, new FileSystemStreamingSink(_directory.FullName, "rendition_a_"));

            AddSeconds(segmenter, 2);
            segmenter.Complete();

            var playlist = File.ReadAllText(Path.Combine(_directory.FullName, "rendition_a_playlist.m3u8"));
            playlist.Should().Contain("#EXT-X-MAP:URI=\"rendition_a_init.mp4\"\n");
            var uris = PlaylistUris("rendition_a_playlist.m3u8");
            uris.Should().Equal("rendition_a_segment_1.m4s", "rendition_a_segment_2.m4s");
            uris.Append("rendition_a_init.mp4").Should().OnlyContain(uri => File.Exists(Path.Combine(_directory.FullName, uri)));
        }

        [Fact]
        public void HlsSegmenter_Live_WithFilePrefixAndMpegTs_Should_DeleteTheAgedOutFile()
        {
            var segmenter = new HlsSegmenter(1, SampleRate, 1, new FileSystemStreamingSink(_directory.FullName, "low_"), liveWindowSize: 2, SegmentFormat.MpegTs);

            AddSeconds(segmenter, 4);

            File.Exists(Path.Combine(_directory.FullName, "low_segment_1.ts")).Should().BeFalse();
            PlaylistUris("low_playlist.m3u8").Should().Equal("low_segment_2.ts", "low_segment_3.ts")
                .And.OnlyContain(uri => File.Exists(Path.Combine(_directory.FullName, uri)));
        }

        [Fact]
        public async Task HlsMediaPacketSink_WithFilePrefix_Should_WriteAPlaylistWhoseUrisAllExist()
        {
            var capture = HlsPacketSegmenterTest.FiveSecondCapture();
            await using var hls = new HlsMediaPacketSink(new FileSystemStreamingSink(_directory.FullName, "video_"), 2);

            await hls.OpenAsync(capture.Demuxer.Tracks!);
            foreach (var packet in capture.Packets)
            {
                await hls.WriteAsync(packet);
            }

            await hls.CompleteAsync();

            File.ReadAllText(Path.Combine(_directory.FullName, "video_playlist.m3u8")).Should().Contain("#EXT-X-MAP:URI=\"video_init.mp4\"\n");
            PlaylistUris("video_playlist.m3u8").Should().Equal("video_segment_1.m4s", "video_segment_2.m4s", "video_segment_3.m4s")
                .And.OnlyContain(uri => File.Exists(Path.Combine(_directory.FullName, uri)));
        }

        [Fact]
        public void FileSystemStreamingSink_NamedOverloads_Should_WritePrefixedFiles_And_ReturnTheirUris()
        {
            IStreamingSink sink = new FileSystemStreamingSink(_directory.FullName, "p_");

            var initUri = sink.WriteInitSegment("init.mp4", [1]);
            var segmentUri = sink.WriteMediaSegment(4, "segment_4.ts", [2, 3]);

            initUri.Should().Be("p_init.mp4");
            segmentUri.Should().Be("p_segment_4.ts");
            File.ReadAllBytes(Path.Combine(_directory.FullName, "p_init.mp4")).Should().Equal(1);
            File.ReadAllBytes(Path.Combine(_directory.FullName, "p_segment_4.ts")).Should().Equal(2, 3);

            sink.DeleteMediaSegment(4, segmentUri);
            sink.DeleteMediaSegment(4, segmentUri);

            File.Exists(Path.Combine(_directory.FullName, "p_segment_4.ts")).Should().BeFalse();
        }

        [Fact]
        public void FileSystemStreamingSink_UnnamedOverloads_Should_KeepTheirFileNames()
        {
            var sink = new FileSystemStreamingSink(_directory.FullName, "p_");

            sink.WriteInitSegment([1]);
            sink.WriteMediaSegment(4, [2]);

            File.Exists(Path.Combine(_directory.FullName, "p_init.mp4")).Should().BeTrue();
            File.Exists(Path.Combine(_directory.FullName, "p_segment_4.m4s")).Should().BeTrue();
            sink.DeleteMediaSegment(4);
            File.Exists(Path.Combine(_directory.FullName, "p_segment_4.m4s")).Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("../escape.ts")]
        [InlineData("sub/segment_1.ts")]
        public void FileSystemStreamingSink_NamedOverloads_WithoutABareFileName_Should_Throw(string? fileName)
        {
            IStreamingSink sink = new FileSystemStreamingSink(_directory.FullName, "p_");

            var writeSegment = () => sink.WriteMediaSegment(1, fileName!, [1]);
            var writeInit = () => sink.WriteInitSegment(fileName!, [1]);

            writeSegment.Should().Throw<ArgumentException>();
            writeInit.Should().Throw<ArgumentException>();
            Directory.GetFiles(_directory.Parent!.FullName, "escape.ts").Should().BeEmpty();
            Directory.EnumerateFileSystemEntries(_directory.FullName).Should().BeEmpty();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("q_segment_1.ts")]
        [InlineData("p_../escape.ts")]
        [InlineData("p_")]
        public void FileSystemStreamingSink_DeleteMediaSegment_WithAUriItDidNotReturn_Should_Throw(string? uri)
        {
            IStreamingSink sink = new FileSystemStreamingSink(_directory.FullName, "p_");

            var act = () => sink.DeleteMediaSegment(1, uri!);

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public void HlsSegmenter_SinkReturningNoUri_Should_Throw(SegmentFormat format)
        {
            var sink = new DelegateStreamingSink(_ => { }, (_, _) => { }, _ => { }, _ => { });
            var noUriSink = new NoUriSink(sink);
            var act = () =>
            {
                var segmenter = new HlsSegmenter(1, SampleRate, 1, noUriSink, format);
                AddSeconds(segmenter, 2);
            };

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void HlsSegmenter_PacketMpegTs_WithFilePrefix_Should_ListOnlyFilesThatExist()
        {
            var capture = HlsPacketSegmenterTest.FiveSecondCapture();
            var segmenter = new HlsSegmenter(capture.Demuxer.Tracks!, 2, new FileSystemStreamingSink(_directory.FullName, "ts_"), SegmentFormat.MpegTs);

            capture.Packets.ForEach(segmenter.AddPacket);
            segmenter.Complete();

            PlaylistUris("ts_playlist.m3u8").Should().Equal("ts_segment_1.ts", "ts_segment_2.ts", "ts_segment_3.ts")
                .And.OnlyContain(uri => File.Exists(Path.Combine(_directory.FullName, uri)));
        }

        [Fact]
        public void IStreamingSink_DefaultNamedOverloads_Should_CallTheOriginalMembers_And_ReturnTheFileName()
        {
            var inner = new InMemoryStreamingSink();
            IStreamingSink sink = inner;

            sink.WriteInitSegment("init.mp4", [1]).Should().Be("init.mp4");
            sink.WriteMediaSegment(7, "segment_7.ts", [2]).Should().Be("segment_7.ts");
            inner.InitSegment.Should().Equal(1);
            inner.MediaSegments[7].Should().Equal(2);

            sink.DeleteMediaSegment(7, "segment_7.ts");

            inner.MediaSegments.Should().BeEmpty();
        }

        private static void AddSeconds(HlsSegmenter segmenter, int seconds)
        {
            var frames = (int)Math.Ceiling(seconds * SampleRate / (double)SamplesPerFrame);
            for (var i = 0; i < frames; i++)
            {
                segmenter.AddFrame(new EncodedAudioFrame(new byte[] { 0x21, 0x10 }, SamplesPerFrame, i));
            }
        }

        private sealed class NoUriSink(IStreamingSink inner) : IStreamingSink
        {
            public void WriteInitSegment(ReadOnlySpan<byte> bytes) => inner.WriteInitSegment(bytes);

            public void WriteMediaSegment(long sequenceNumber, ReadOnlySpan<byte> bytes) => inner.WriteMediaSegment(sequenceNumber, bytes);

            public void WritePlaylist(ReadOnlySpan<byte> utf8Bytes) => inner.WritePlaylist(utf8Bytes);

            public void DeleteMediaSegment(long sequenceNumber) => inner.DeleteMediaSegment(sequenceNumber);

            public string WriteInitSegment(string fileName, ReadOnlySpan<byte> bytes) => string.Empty;

            public string WriteMediaSegment(long sequenceNumber, string fileName, ReadOnlySpan<byte> bytes) => string.Empty;
        }

        private List<string> PlaylistUris(string playlistFileName) =>
            File.ReadAllText(Path.Combine(_directory.FullName, playlistFileName), Encoding.UTF8)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith('#'))
                .ToList();
    }
}
