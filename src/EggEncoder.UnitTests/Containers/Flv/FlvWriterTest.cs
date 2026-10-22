using System.Diagnostics;
using EggEncoder.Containers.Flv;
using EggEncoder.Live.Rtmp.Amf0;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Flv
{
    public class FlvWriterTest
    {
        private static MediaTrackInfo AvcTrack(int id = 0, int version = 0, byte[]? config = null) =>
            new(id, MediaKind.Video, MediaCodec.H264, 1000, config ?? FlvTestBuilder.AvcC, version, width: 64, height: 64);

        private static MediaTrackInfo AacTrack(int id = 1, int timescale = 1000) =>
            new(id, MediaKind.Audio, MediaCodec.Aac, timescale, FlvTestBuilder.AacConfig, 0, 44100, 1);

        private static byte[] WriteAll(MediaTrackSet tracks, IEnumerable<MediaPacket> packets, FlvMetadata? metadata = null)
        {
            using var stream = new MemoryStream();
            using (var writer = new FlvWriter(stream, leaveOpen: true, metadata))
            {
                writer.Open(tracks);
                foreach (var packet in packets)
                {
                    writer.Write(packet);
                }

                writer.Complete();
            }

            return stream.ToArray();
        }

        private static string Describe(MediaPacket p) =>
            $"{p.TrackId}:{p.Pts}:{p.Dts}:{p.IsKeyframe}:{p.ConfigVersion}:{Convert.ToHexString(p.Payload.Span)}";

        [Theory]
        [InlineData("h264_aac.flv")]
        [InlineData("hevc.flv")]
        [InlineData("mp3.flv")]
        public void RoundTrip_FfmpegFixture_Should_Be_Packet_Identical(string fixture)
        {
            var original = FlvDemuxerTest.Demux(File.ReadAllBytes(Path.GetFullPath($"Containers/Flv/{fixture}")));

            var written = WriteAll(original.Demuxer.Tracks!, original.Packets);
            var reread = FlvDemuxerTest.Demux(written);

            reread.Packets.Select(Describe).Should().Equal(original.Packets.Select(Describe));
            reread.Demuxer.Tracks!.Tracks.Select(t => (t.TrackId, t.Codec, t.SampleRate, t.Channels, t.Width, t.Height, Convert.ToHexString(t.CodecConfiguration.Span)))
                .Should().Equal(original.Demuxer.Tracks!.Tracks.Select(t => (t.TrackId, t.Codec, t.SampleRate, t.Channels, t.Width, t.Height, Convert.ToHexString(t.CodecConfiguration.Span))));
            reread.Demuxer.SkippedTagCount.Should().Be(0);
        }

        [Fact]
        public void Write_H264AacFixture_Should_Write_Default_Metadata()
        {
            var original = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));

            var metadata = FlvDemuxerTest.Demux(WriteAll(original.Demuxer.Tracks!, original.Packets)).Demuxer.Metadata!;

            metadata.Width.Should().Be(64);
            metadata.Height.Should().Be(64);
            metadata.AudioSampleRate.Should().Be(44100);
            metadata.Duration.Should().Be(0);
            metadata.Encoder.Should().Be("EggEncoder");
            metadata.Find("videocodecid")!.Number.Should().Be(7);
            metadata.Find("audiocodecid")!.Number.Should().Be(10);
            metadata.Find("stereo")!.Boolean.Should().BeFalse();
        }

        [Fact]
        public void Write_CustomMetadata_Should_Be_Written_Verbatim()
        {
            var custom = new FlvMetadata([new("title", Amf0Value.FromString("x")), new("framerate", Amf0Value.FromNumber(30))]);

            var bytes = WriteAll(new MediaTrackSet([AacTrack()]), [], custom);
            var metadata = FlvDemuxerTest.Demux(bytes).Demuxer.Metadata!;

            metadata.Properties.Select(p => p.Key).Should().Equal("title", "framerate");
            metadata.FrameRate.Should().Be(30);
        }

        [Fact]
        public void Open_UnencodableMetadata_Should_Throw_Before_Writing_Anything()
        {
            var stream = new MemoryStream();
            var metadata = new FlvMetadata([new(new string('k', ushort.MaxValue + 1), Amf0Value.Null)]);
            using var writer = new FlvWriter(stream, leaveOpen: true, metadata);

            var act = () => writer.Open(new MediaTrackSet([AacTrack()]));

            act.Should().Throw<ArgumentException>();
            stream.Length.Should().Be(0);
        }

        [Fact]
        public void Write_Header_Should_Declare_Only_Present_Kinds()
        {
            var audioOnly = WriteAll(new MediaTrackSet([AacTrack()]), []);
            var videoOnly = WriteAll(new MediaTrackSet([AvcTrack()]), []);
            var both = WriteAll(new MediaTrackSet([AvcTrack(), AacTrack()]), []);

            audioOnly[4].Should().Be(0x04);
            videoOnly[4].Should().Be(0x01);
            both[4].Should().Be(0x05);
            audioOnly[..13].Should().Equal((byte)'F', (byte)'L', (byte)'V', 1, 4, 0, 0, 0, 9, 0, 0, 0, 0);
        }

        [Fact]
        public void Write_Should_Rescale_Timestamps_To_Milliseconds()
        {
            var track = AacTrack(timescale: 44100);

            var bytes = WriteAll(new MediaTrackSet([track]), [new MediaPacket(1, 44100, 44100, true, 0, new byte[] { 1 })]);

            FlvDemuxerTest.Demux(bytes).Packets.Single().Dts.Should().Be(1000);
        }

        [Fact]
        public void Write_NegativeCompositionOffset_Should_RoundTrip()
        {
            var packet = new MediaPacket(0, 60, 100, false, 0, new byte[] { 0, 0, 0, 1, 0x41 });

            var reread = FlvDemuxerTest.Demux(WriteAll(new MediaTrackSet([AvcTrack()]), [packet]));

            reread.Packets.Single().CompositionOffset.Should().Be(-40);
        }

        [Fact]
        public void Write_TimestampPast32Bits_Should_Wrap_And_Demux_Should_Unwrap()
        {
            var track = AacTrack();
            var packets = new[]
            {
                new MediaPacket(1, 0xFFFFFFF0L, 0xFFFFFFF0L, true, 0, new byte[] { 1 }),
                new MediaPacket(1, 0x100000010L, 0x100000010L, true, 0, new byte[] { 2 })
            };

            var reread = FlvDemuxerTest.Demux(WriteAll(new MediaTrackSet([track]), packets));

            reread.Packets.Select(p => p.Dts).Should().Equal(0xFFFFFFF0L, 0x100000010L);
        }

        [Fact]
        public void UpdateTracks_ConfigChange_Should_Write_New_Sequence_Header()
        {
            var otherConfig = Convert.FromHexString("1210");
            using var stream = new MemoryStream();
            using (var writer = new FlvWriter(stream, leaveOpen: true))
            {
                writer.Open(new MediaTrackSet([AacTrack()]));
                writer.Write(new MediaPacket(1, 0, 0, true, 0, new byte[] { 1 }));
                writer.UpdateTracks(new MediaTrackSet([AacTrack() with { CodecConfiguration = otherConfig, ConfigVersion = 1 }]));
                writer.Write(new MediaPacket(1, 23, 23, true, 1, new byte[] { 2 }));
                writer.Complete();
            }

            var reread = FlvDemuxerTest.Demux(stream.ToArray());

            reread.TrackSets.Should().HaveCount(2);
            reread.TrackSets[1].Tracks.Single().CodecConfiguration.ToArray().Should().Equal(otherConfig);
            reread.Packets.Select(p => p.ConfigVersion).Should().Equal(0, 1);
        }

        [Fact]
        public void Write_HevcWithCompositionOffset_Should_Use_CodedFrames_And_Without_Use_CodedFramesX()
        {
            var hvcC = FlvDemuxerTest.HevcConfig();
            var track = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H265, 1000, hvcC, 0);

            var bytes = WriteAll(new MediaTrackSet([track]),
            [
                new MediaPacket(0, 80, 0, true, 0, new byte[] { 0, 0, 0, 1, 0x26 }),
                new MediaPacket(0, 40, 40, false, 0, new byte[] { 0, 0, 0, 1, 0x02 })
            ]);

            var tags = ReadTagBodies(bytes).Where(t => t.Type == 9).Select(t => t.Body[0]).ToList();
            tags.Should().Equal(0x90, 0x91, 0xA3);
            FlvDemuxerTest.Demux(bytes).Packets.Select(p => (p.Pts, p.Dts)).Should().Equal((80L, 0L), (40L, 40L));
        }

        [Theory]
        [InlineData(MediaCodec.Av1, "av01")]
        [InlineData(MediaCodec.Vp9, "vp09")]
        public void Write_Av1AndVp9_Should_RoundTrip_As_Enhanced_Tags(MediaCodec codec, string fourCc)
        {
            var track = new MediaTrackInfo(0, MediaKind.Video, codec, 1000, new byte[] { 0x81, 0, 0x0C, 0 }, 0);
            var packet = new MediaPacket(0, 40, 40, true, 0, new byte[] { 0x12, 0 });

            var bytes = WriteAll(new MediaTrackSet([track]), [packet]);
            var reread = FlvDemuxerTest.Demux(bytes);

            ReadTagBodies(bytes).Where(t => t.Type == 9).Select(t => System.Text.Encoding.ASCII.GetString(t.Body, 1, 4)).Should().AllBe(fourCc);
            reread.Packets.Single().Payload.ToArray().Should().Equal(0x12, 0);
            reread.Demuxer.Tracks!.Tracks.Single().Codec.Should().Be(codec);
            reread.Demuxer.Metadata!.Find("videocodecid")!.Number.Should().Be((double)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(fourCc)));
        }

        [Theory]
        [InlineData(48000, 2, 0x2F)]
        [InlineData(22050, 1, 0x2A)]
        [InlineData(11025, 2, 0x27)]
        [InlineData(8000, 1, 0xE2)]
        [InlineData(5512, 1, 0x22)]
        public void Write_Mp3_Should_Encode_Rate_And_Channel_Hints(int sampleRate, int channels, byte expectedHeader)
        {
            var track = new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Mp3, 1000, ReadOnlyMemory<byte>.Empty, 0, sampleRate, channels);

            var bytes = WriteAll(new MediaTrackSet([track]), [new MediaPacket(1, 0, 0, true, 0, new byte[] { 0xFF, 0xFB })]);

            ReadTagBodies(bytes).Where(t => t.Type == 8).Should().ContainSingle().Which.Body[0].Should().Be(expectedHeader);
        }

        [Fact]
        public void Open_UnsupportedTrackSets_Should_Throw()
        {
            var opus = new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Opus, 48000, ReadOnlyMemory<byte>.Empty, 0);
            var data = new MediaTrackInfo(2, MediaKind.Data, MediaCodec.Aac, 1000, ReadOnlyMemory<byte>.Empty, 0);
            var aacVideo = new MediaTrackInfo(3, MediaKind.Video, MediaCodec.Aac, 1000, ReadOnlyMemory<byte>.Empty, 0);

            OpenAction(new MediaTrackSet([opus])).Should().Throw<NotSupportedException>().WithMessage("*Opus*");
            OpenAction(new MediaTrackSet([data])).Should().Throw<NotSupportedException>().WithMessage("*data track*");
            OpenAction(new MediaTrackSet([aacVideo])).Should().Throw<NotSupportedException>();
            OpenAction(new MediaTrackSet([AacTrack(1), AacTrack(2)])).Should().Throw<NotSupportedException>().WithMessage("*at most one*");
            OpenAction(new MediaTrackSet([AvcTrack(1), AvcTrack(2)])).Should().Throw<NotSupportedException>().WithMessage("*at most one*");
        }

        private static Action OpenAction(MediaTrackSet tracks) => () =>
        {
            using var writer = new FlvWriter(new MemoryStream());
            writer.Open(tracks);
        };

        [Fact]
        public void Write_InvalidPackets_Should_Throw()
        {
            using var writer = new FlvWriter(new MemoryStream());
            var hevc = new MediaTrackInfo(2, MediaKind.Video, MediaCodec.Av1, 1000, new byte[] { 1 }, 0);
            writer.Open(new MediaTrackSet([AacTrack(), hevc]));

            ((Action)(() => writer.Write(new MediaPacket(9, 0, 0, true, 0, new byte[1])))).Should().Throw<ArgumentException>().WithMessage("*not in the open track set*");
            ((Action)(() => writer.Write(new MediaPacket(1, 0, 0, true, 5, new byte[1])))).Should().Throw<ArgumentException>().WithMessage("*UpdateTracks*");
            ((Action)(() => writer.Write(new MediaPacket(1, -1, -1, true, 0, new byte[1])))).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*negative*");
            ((Action)(() => writer.Write(new MediaPacket(1, 10, 0, true, 0, new byte[1])))).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*audio*");
            ((Action)(() => writer.Write(new MediaPacket(2, 10, 0, true, 0, new byte[1])))).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Av1*");
            ((Action)(() => writer.Write(new MediaPacket(1, 1L << 24, 0, true, 0, new byte[1])))).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*24-bit*");
            ((Action)(() => writer.Write(new MediaPacket(1, -(1L << 24), 0, true, 0, new byte[1])))).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*24-bit*");
        }

        [Fact]
        public void RoundTrip_PacketLargerThanEveryInternalBuffer_Should_Be_Identical()
        {
            var payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray();
            var packet = new MediaPacket(1, 0, 0, true, 0, payload);

            var bytes = WriteAll(new MediaTrackSet([AacTrack()]), [packet]);

            FlvDemuxerTest.Demux(bytes, chunkSize: 5000).Packets.Single().Payload.ToArray().Should().Equal(payload);
            FlvDemuxerTest.Demux(bytes).Packets.Single().Payload.ToArray().Should().Equal(payload);
        }

        [Fact]
        public void Write_TagBodyOver24Bits_Should_Throw()
        {
            using var writer = new FlvWriter(new MemoryStream());
            writer.Open(new MediaTrackSet([AacTrack()]));

            var act = () => writer.Write(new MediaPacket(1, 0, 0, true, 0, new byte[0x1000000]));

            act.Should().Throw<ArgumentException>().WithMessage("*24-bit DataSize*");
        }

        [Fact]
        public void UpdateTracks_UnknownTrackOrKindChange_Should_Throw()
        {
            using var writer = new FlvWriter(new MemoryStream());
            writer.Open(new MediaTrackSet([AacTrack()]));

            ((Action)(() => writer.UpdateTracks(new MediaTrackSet([AvcTrack(1)])))).Should().Throw<ArgumentException>();
            ((Action)(() => writer.UpdateTracks(new MediaTrackSet([AvcTrack(7)])))).Should().Throw<ArgumentException>();
            ((Action)(() => writer.UpdateTracks(null!))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Lifecycle_Misuse_Should_Throw()
        {
            var writer = new FlvWriter(new MemoryStream());
            var tracks = new MediaTrackSet([AacTrack()]);

            ((Action)(() => writer.Write(new MediaPacket(1, 0, 0, true, 0, new byte[1])))).Should().Throw<InvalidOperationException>().WithMessage("*Open*");
            ((Action)(() => writer.Complete())).Should().Throw<InvalidOperationException>();
            ((Action)(() => writer.UpdateTracks(tracks))).Should().Throw<InvalidOperationException>();
            ((Action)(() => writer.Open(null!))).Should().Throw<ArgumentNullException>();
            writer.Open(tracks);
            ((Action)(() => writer.Open(tracks))).Should().Throw<InvalidOperationException>().WithMessage("*already open*");
            writer.Complete();
            ((Action)(() => writer.Write(new MediaPacket(1, 0, 0, true, 0, new byte[1])))).Should().Throw<InvalidOperationException>().WithMessage("*completed*");
            writer.Dispose();
            writer.Dispose();
            ((Action)(() => writer.Open(tracks))).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void Constructor_InvalidDestination_Should_Throw()
        {
            ((Action)(() => new FlvWriter(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new FlvWriter(new MemoryStream([], writable: false)))).Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Dispose_Should_Close_Destination_Unless_LeaveOpen()
        {
            var owned = new MemoryStream();
            var kept = new MemoryStream();

            new FlvWriter(owned).Dispose();
            new FlvWriter(kept, leaveOpen: true).Dispose();

            owned.CanWrite.Should().BeFalse();
            kept.CanWrite.Should().BeTrue();
        }

        [Fact]
        public async Task AsyncSink_Should_Produce_The_Same_Bytes_As_The_Sync_Core()
        {
            var tracks = new MediaTrackSet([AacTrack()]);
            var packet = new MediaPacket(1, 0, 0, true, 0, new byte[] { 0x21 });
            var expected = WriteAll(tracks, [packet]);
            var stream = new MemoryStream();

            await using (IMediaPacketSink sink = new FlvWriter(stream, leaveOpen: true))
            {
                await sink.OpenAsync(tracks);
                await sink.WriteAsync(packet);
                await sink.CompleteAsync();
            }

            stream.ToArray().Should().Equal(expected);
        }

        [Fact]
        public async Task AsyncSink_CanceledToken_Should_Throw_Before_Writing()
        {
            var stream = new MemoryStream();
            await using var writer = new FlvWriter(stream, leaveOpen: true);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await FluentActions.Awaiting(() => writer.OpenAsync(new MediaTrackSet([AacTrack()]), cancellation.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
            stream.Length.Should().Be(0);
            await writer.OpenAsync(new MediaTrackSet([AacTrack()]));
            await FluentActions.Awaiting(() => writer.WriteAsync(new MediaPacket(1, 0, 0, true, 0, new byte[1]), cancellation.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => writer.CompleteAsync(cancellation.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void FlvMetadata_FromTracks_Should_Skip_Unknown_Values_And_Optional_Encoder()
        {
            var video = new MediaTrackInfo(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, 0);
            var audio = new MediaTrackInfo(1, MediaKind.Audio, MediaCodec.Mp3, 1000, ReadOnlyMemory<byte>.Empty, 0);

            var metadata = FlvMetadata.FromTracks(new MediaTrackSet([video, audio]), encoder: null);

            metadata.Properties.Select(p => p.Key).Should().Equal("duration", "videocodecid", "audiocodecid");
            metadata.Encoder.Should().BeNull();
            metadata.Width.Should().BeNull();
            ((Action)(() => FlvMetadata.FromTracks(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new FlvMetadata(null!))).Should().Throw<ArgumentNullException>();
            ((Action)(() => metadata.Find(null!))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void FlvMetadata_TypedAccessors_Should_Ignore_Wrong_Types()
        {
            var metadata = new FlvMetadata([new("width", Amf0Value.FromString("64")), new("encoder", Amf0Value.FromNumber(1))]);

            metadata.Width.Should().BeNull();
            metadata.Encoder.Should().BeNull();
            new FlvMetadata([new("encoder", Amf0Value.FromLongString("x"))]).Encoder.Should().Be("x");
        }

        // Env-gated cross-check: set EGGENCODER_FFMPEG_ORACLE=1 with ffprobe on PATH to run it; passes
        // trivially otherwise, so CI needs no ffmpeg.
        [Fact]
        public void Ffprobe_OnWrittenFlv_Should_Report_The_Expected_Streams()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            var original = FlvDemuxerTest.Demux(File.ReadAllBytes(FlvDemuxerTest.H264AacFixture));
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-flv-{Guid.NewGuid():N}.flv");
            try
            {
                File.WriteAllBytes(path, WriteAll(original.Demuxer.Tracks!, original.Packets));
                var startInfo = new ProcessStartInfo("ffprobe", $"-v error -show_entries stream=codec_name,width,height,sample_rate,channels -show_entries packet=stream_index -of compact=p=0 \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                process.ExitCode.Should().Be(0);
                output.Should().Contain("codec_name=h264|width=64|height=64");
                output.Should().Contain("codec_name=aac|sample_rate=44100|channels=1");
                output.Split('\n').Count(l => l.StartsWith("stream_index=")).Should().Be(original.Packets.Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        internal static List<(byte Type, byte[] Body)> ReadTagBodies(byte[] bytes)
        {
            var tags = new List<(byte, byte[])>();
            var position = 13;
            while (position + 11 <= bytes.Length)
            {
                var size = bytes[position + 1] << 16 | bytes[position + 2] << 8 | bytes[position + 3];
                tags.Add((bytes[position], bytes[(position + 11)..(position + 11 + size)]));
                position += 11 + size + 4;
            }

            return tags;
        }
    }
}
