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
        public async Task ReadPacketsAsync_ConfigChangeWithinOneRead_Should_Not_Advance_Tracks_Early()
        {
            // Regression: Tracks used to read the demuxer's live set, which already held version 1
            // while the version-0 packets from the same 64 KiB read were still being yielded.
            var bytes = new FlvTestBuilder(flags: 0x04)
                .AacSequenceHeader()
                .AacFrame(0, 1)
                .AacFrame(23, 2)
                .AacSequenceHeader(46, Convert.FromHexString("1210"))
                .AacFrame(46, 3)
                .ToArray();
            await using var source = new FlvFileSource(new MemoryStream(bytes));

            var seen = new List<(int PacketVersion, int TrackVersion, string Config)>();
            await foreach (var packet in source.ReadPacketsAsync())
            {
                var track = source.Tracks!.FindTrack(packet.TrackId)!;
                seen.Add((packet.ConfigVersion, track.ConfigVersion, Convert.ToHexString(track.CodecConfiguration.Span)));
            }

            seen.Should().Equal((0, 0, "120856E500"), (0, 0, "120856E500"), (1, 1, "1210"));
        }

        [Fact]
        public async Task ReadPacketsAsync_RemuxThroughWriter_With_Config_Change_Should_Succeed()
        {
            var bytes = new FlvTestBuilder(flags: 0x04)
                .AacSequenceHeader()
                .AacFrame(0, 1)
                .AacSequenceHeader(23, Convert.FromHexString("1210"))
                .AacFrame(23, 2)
                .ToArray();
            await using var source = new FlvFileSource(new MemoryStream(bytes));
            var output = new MemoryStream();
            await using var writer = new FlvWriter(output, leaveOpen: true);

            MediaTrackSet? written = null;
            await foreach (var packet in source.ReadPacketsAsync())
            {
                if (written is null)
                {
                    await writer.OpenAsync(source.Tracks!);
                }
                else if (!ReferenceEquals(written, source.Tracks))
                {
                    writer.UpdateTracks(source.Tracks!);
                }

                written = source.Tracks;
                await writer.WriteAsync(packet);
            }

            await writer.CompleteAsync();

            FlvDemuxerTest.Demux(output.ToArray()).Packets.Select(p => p.ConfigVersion).Should().Equal(0, 1);
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
