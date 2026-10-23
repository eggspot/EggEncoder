using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using EggEncoder.Streaming;
using EggEncoder.Streaming.Hls;
using EggEncoder.UnitTests.Containers.Flv;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Hls
{
    public class HlsMediaPacketSinkTest
    {
        [Theory]
        [InlineData(SegmentFormat.Fmp4)]
        [InlineData(SegmentFormat.MpegTs)]
        public async Task FlvFileSource_IntoTheSink_Should_MatchTheSegmenterFedDirectly(SegmentFormat format)
        {
            var sink = new InMemoryStreamingSink();
            await using (var source = new FlvFileSource(File.OpenRead(HlsPacketSegmenterTest.FiveSecondFixture)))
            await using (var packetSink = new HlsMediaPacketSink(sink, 2, format))
            {
                await foreach (var packet in source.ReadPacketsAsync())
                {
                    if (packetSink.Segmenter is null)
                    {
                        await packetSink.OpenAsync(source.Tracks!);
                    }

                    await packetSink.WriteAsync(packet);
                }

                await packetSink.CompleteAsync();
            }

            var capture = HlsPacketSegmenterTest.FiveSecondCapture();
            var expected = new InMemoryStreamingSink();
            var segmenter = new HlsSegmenter(capture.Demuxer.Tracks!, 2, expected, format);
            capture.Packets.ForEach(segmenter.AddPacket);
            segmenter.Complete();

            sink.InitSegment.Should().Equal(expected.InitSegment);
            sink.MediaSegments.Keys.Should().Equal(expected.MediaSegments.Keys);
            foreach (var (sequenceNumber, bytes) in expected.MediaSegments)
            {
                sink.MediaSegments[sequenceNumber].Should().Equal(bytes);
            }

            sink.Playlist.Should().Equal(expected.Playlist);
        }

        [Fact]
        public async Task LiveWindow_Should_ReachTheSegmenter()
        {
            var sink = new InMemoryStreamingSink();
            await using var packetSink = new HlsMediaPacketSink(sink, 1, SegmentFormat.MpegTs, liveWindowSize: 1);
            var capture = HlsPacketSegmenterTest.FiveSecondCapture();

            await packetSink.OpenAsync(capture.Demuxer.Tracks!);
            foreach (var packet in capture.Packets)
            {
                await packetSink.WriteAsync(packet);
            }

            await packetSink.CompleteAsync();

            sink.MediaSegments.Keys.Should().Equal(5L);
            HlsPacketSegmenterTest.PlaylistText(sink).Should().Contain("#EXT-X-MEDIA-SEQUENCE:5\n");
        }

        [Fact]
        public void Constructor_NullSink_Should_Throw()
        {
            var act = () => new HlsMediaPacketSink(null!, 2);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void Constructor_NonPositiveTargetDuration_Should_Throw()
        {
            var act = () => new HlsMediaPacketSink(new InMemoryStreamingSink(), 0);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_LiveWindowBelowOne_Should_Throw()
        {
            var act = () => new HlsMediaPacketSink(new InMemoryStreamingSink(), 2, liveWindowSize: 0);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task OpenAsync_Should_BuildTheSegmenter_And_WriteTheInitSegment()
        {
            var sink = new InMemoryStreamingSink();
            await using var packetSink = new HlsMediaPacketSink(sink, 2);

            packetSink.Segmenter.Should().BeNull();
            await packetSink.OpenAsync(Tracks());

            packetSink.Segmenter.Should().NotBeNull();
            sink.InitSegment.Should().NotBeNull();
        }

        [Fact]
        public async Task OpenAsync_Twice_Should_Throw()
        {
            await using var packetSink = new HlsMediaPacketSink(new InMemoryStreamingSink(), 2);
            await packetSink.OpenAsync(Tracks());

            var act = async () => await packetSink.OpenAsync(Tracks());

            await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task OpenAsync_InvalidTracks_Should_Throw_And_LeaveTheSinkUnopened()
        {
            await using var packetSink = new HlsMediaPacketSink(new InMemoryStreamingSink(), 2);

            var act = async () => await packetSink.OpenAsync(new MediaTrackSet([]));

            await act.Should().ThrowExactlyAsync<ArgumentException>();
            packetSink.Segmenter.Should().BeNull();
        }

        [Fact]
        public async Task WriteAsync_BeforeOpen_Should_Throw()
        {
            await using var packetSink = new HlsMediaPacketSink(new InMemoryStreamingSink(), 2);

            var act = async () => await packetSink.WriteAsync(KeyframePacket());

            await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task CompleteAsync_BeforeOpen_Should_Throw()
        {
            await using var packetSink = new HlsMediaPacketSink(new InMemoryStreamingSink(), 2);

            var act = async () => await packetSink.CompleteAsync();

            await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task CanceledToken_Should_Throw_Before_Doing_Anything()
        {
            var sink = new InMemoryStreamingSink();
            await using var packetSink = new HlsMediaPacketSink(sink, 2);
            var canceled = new CancellationToken(canceled: true);

            var open = async () => await packetSink.OpenAsync(Tracks(), canceled);
            await open.Should().ThrowAsync<OperationCanceledException>();
            sink.InitSegment.Should().BeNull();

            await packetSink.OpenAsync(Tracks());
            var write = async () => await packetSink.WriteAsync(KeyframePacket(), canceled);
            var complete = async () => await packetSink.CompleteAsync(canceled);

            await write.Should().ThrowAsync<OperationCanceledException>();
            await complete.Should().ThrowAsync<OperationCanceledException>();
            sink.Playlist.Should().BeNull();
        }

        [Fact]
        public async Task AfterDispose_Every_Call_Should_Throw()
        {
            var packetSink = new HlsMediaPacketSink(new InMemoryStreamingSink(), 2);
            await packetSink.DisposeAsync();
            await packetSink.DisposeAsync();

            var open = async () => await packetSink.OpenAsync(Tracks());
            var write = async () => await packetSink.WriteAsync(KeyframePacket());
            var complete = async () => await packetSink.CompleteAsync();

            await open.Should().ThrowExactlyAsync<ObjectDisposedException>();
            await write.Should().ThrowExactlyAsync<ObjectDisposedException>();
            await complete.Should().ThrowExactlyAsync<ObjectDisposedException>();
        }

        private static MediaTrackSet Tracks() =>
            new([new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0)]);

        private static MediaPacket KeyframePacket() => new(0, 0, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x65 });
    }
}
