using EggEncoder.Containers.Flv;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Flv
{
    public class FlvFileSourceTest
    {
        [Fact]
        public async Task ReadPacketsAsync_Fixture_Should_Yield_Every_Packet_In_Order()
        {
            var expected = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture)).Packets;
            await using var source = new FlvFileSource(File.OpenRead(FlvDemuxerTest.H264AacFixture));

            var packets = new List<MediaPacket>();
            await foreach (var packet in source.ReadPacketsAsync())
            {
                source.Tracks.Should().NotBeNull();
                packets.Add(packet);
            }

            packets.Select(p => (p.TrackId, p.Pts, p.Dts)).Should().Equal(expected.Select(p => (p.TrackId, p.Pts, p.Dts)));
            source.Tracks!.Tracks.Should().HaveCount(2);
            source.Metadata!.Width.Should().Be(64);
            source.SkippedTagCount.Should().Be(0);
        }

        [Fact]
        public async Task ReadPacketsAsync_NonSeekableStream_Should_Work()
        {
            var bytes = File.ReadAllBytes(FlvDemuxerTest.Mp3Fixture);
            await using var source = new FlvFileSource(new NonSeekableStream(bytes));

            var count = 0;
            await foreach (var _ in source.ReadPacketsAsync())
            {
                count++;
            }

            count.Should().Be(9);
        }

        [Fact]
        public async Task ReadPacketsAsync_Twice_Should_Throw()
        {
            await using var source = new FlvFileSource(new MemoryStream(new FlvTestBuilder().ToArray()));
            await foreach (var _ in source.ReadPacketsAsync())
            {
            }

            var act = async () =>
            {
                await foreach (var _ in source.ReadPacketsAsync())
                {
                }
            };

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task ReadPacketsAsync_Truncated_Should_Throw_InvalidDataException()
        {
            var bytes = File.ReadAllBytes(FlvDemuxerTest.H264AacFixture);
            await using var source = new FlvFileSource(new MemoryStream(bytes[..^100]));

            var act = async () =>
            {
                await foreach (var _ in source.ReadPacketsAsync())
                {
                }
            };

            await act.Should().ThrowAsync<InvalidDataException>();
        }

        [Fact]
        public async Task ReadPacketsAsync_Canceled_Should_Throw()
        {
            await using var source = new FlvFileSource(new MemoryStream(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = async () =>
            {
                await foreach (var _ in source.ReadPacketsAsync(cancellation.Token))
                {
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Dispose_Should_Close_Source_Unless_LeaveOpen_And_Reject_Later_Reads()
        {
            var owned = new MemoryStream();
            var kept = new MemoryStream();
            var source = new FlvFileSource(owned);

            source.Dispose();
            source.Dispose();
            new FlvFileSource(kept, leaveOpen: true).Dispose();

            owned.CanRead.Should().BeFalse();
            kept.CanRead.Should().BeTrue();
            var act = () => source.ReadPacketsAsync().GetAsyncEnumerator().MoveNextAsync().AsTask();
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public void Constructor_InvalidSource_Should_Throw()
        {
            ((Action)(() => new FlvFileSource(null!))).Should().Throw<ArgumentNullException>();
            var unreadable = new MemoryStream();
            unreadable.Dispose();
            ((Action)(() => new FlvFileSource(unreadable))).Should().Throw<ArgumentException>();
        }

        private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override bool CanSeek => false;

            public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        }
    }
}
