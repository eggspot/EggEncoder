using EggEncoder.Media;
using EggEncoder.Streaming;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class MediaModelTest
    {
        private static MediaTrackInfo VideoTrack(int trackId = 1) =>
            new(trackId, MediaKind.Video, MediaCodec.H264, 1000, new byte[] { 1, 2 }, 0, width: 1280, height: 720);

        private static MediaTrackInfo AudioTrack(int trackId = 2) =>
            new(trackId, MediaKind.Audio, MediaCodec.Aac, 48000, new byte[] { 0x11, 0x90 }, 3, sampleRate: 48000, channels: 2);

        [Fact]
        public void MediaTrackInfo_ValidArguments_Should_Expose_Every_Field()
        {
            var config = new byte[] { 0x11, 0x90 };
            var track = new MediaTrackInfo(2, MediaKind.Audio, MediaCodec.Aac, 48000, config, 3, 48000, 2, 0, 0);

            track.TrackId.Should().Be(2);
            track.Kind.Should().Be(MediaKind.Audio);
            track.Codec.Should().Be(MediaCodec.Aac);
            track.Timescale.Should().Be(48000);
            track.CodecConfiguration.ToArray().Should().Equal(config);
            track.ConfigVersion.Should().Be(3);
            track.SampleRate.Should().Be(48000);
            track.Channels.Should().Be(2);
            track.Width.Should().Be(0);
            track.Height.Should().Be(0);
        }

        [Fact]
        public void MediaTrackInfo_DefaultOptionalArguments_Should_Be_Zero()
        {
            var track = new MediaTrackInfo(0, MediaKind.Data, MediaCodec.Opus, 1, ReadOnlyMemory<byte>.Empty, 0);

            track.SampleRate.Should().Be(0);
            track.Channels.Should().Be(0);
            track.Width.Should().Be(0);
            track.Height.Should().Be(0);
            track.CodecConfiguration.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void MediaTrackInfo_With_Should_Bump_ConfigVersion_And_Keep_The_Rest()
        {
            var original = VideoTrack();

            var updated = original with { ConfigVersion = 1, CodecConfiguration = new byte[] { 9 } };

            updated.ConfigVersion.Should().Be(1);
            updated.Width.Should().Be(1280);
            updated.Should().NotBe(original);
        }

        [Theory]
        [InlineData(-1, 1000, 0, 0, 0, 0, 0, "TrackId")]
        [InlineData(0, 0, 0, 0, 0, 0, 0, "Timescale")]
        [InlineData(0, -5, 0, 0, 0, 0, 0, "Timescale")]
        [InlineData(0, 1000, -1, 0, 0, 0, 0, "ConfigVersion")]
        [InlineData(0, 1000, 0, -1, 0, 0, 0, "SampleRate")]
        [InlineData(0, 1000, 0, 0, -1, 0, 0, "Channels")]
        [InlineData(0, 1000, 0, 0, 0, -1, 0, "Width")]
        [InlineData(0, 1000, 0, 0, 0, 0, -1, "Height")]
        public void MediaTrackInfo_InvalidArgument_Should_Throw(
            int trackId, int timescale, int configVersion, int sampleRate, int channels, int width, int height, string paramName)
        {
            var act = () => new MediaTrackInfo(trackId, MediaKind.Video, MediaCodec.H264, timescale, ReadOnlyMemory<byte>.Empty, configVersion, sampleRate, channels, width, height);

            act.Should().ThrowExactly<ArgumentOutOfRangeException>().WithParameterName(paramName);
        }

        [Fact]
        public void MediaTrackInfo_With_InvalidValue_Should_Throw()
        {
            var act = () => VideoTrack() with { Timescale = 0 };

            act.Should().ThrowExactly<ArgumentOutOfRangeException>().WithParameterName("Timescale");
        }

        [Fact]
        public void MediaTrackSet_Should_Copy_Tracks_And_Find_By_Id()
        {
            var tracks = new List<MediaTrackInfo> { VideoTrack(), AudioTrack() };
            var set = new MediaTrackSet(tracks);
            tracks.Clear();

            set.Tracks.Should().HaveCount(2);
            set.FindTrack(2)!.Codec.Should().Be(MediaCodec.Aac);
            set.FindTrack(1)!.Codec.Should().Be(MediaCodec.H264);
            set.FindTrack(7).Should().BeNull();
        }

        [Fact]
        public void MediaTrackSet_Empty_Should_Be_Allowed()
        {
            new MediaTrackSet([]).Tracks.Should().BeEmpty();
        }

        [Fact]
        public void MediaTrackSet_DuplicateTrackId_Should_Throw()
        {
            var act = () => new MediaTrackSet([VideoTrack(1), AudioTrack(1)]);

            act.Should().ThrowExactly<ArgumentException>().WithMessage("Duplicate track id 1*");
        }

        [Fact]
        public void MediaTrackSet_NullTrackOrList_Should_Throw()
        {
            var nullTrack = () => new MediaTrackSet([VideoTrack(), null!]);
            var nullList = () => new MediaTrackSet(null!);

            nullTrack.Should().ThrowExactly<ArgumentException>();
            nullList.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void MediaPacket_CompositionOffset_Should_Be_Pts_Minus_Dts()
        {
            var packet = new MediaPacket(1, Pts: 120, Dts: 40, IsKeyframe: false, ConfigVersion: 0, new byte[] { 0, 0, 0, 1, 0x65 });

            packet.CompositionOffset.Should().Be(80);
            (packet with { Dts = 120 }).CompositionOffset.Should().Be(0);
        }

        [Fact]
        public void MediaPacketFactory_FromEncodedAudioFrame_Should_Copy_Payload_And_Mark_Keyframe()
        {
            var buffer = new byte[] { 0x21, 0x10, 0x05 };

            var packet = MediaPacketFactory.FromEncodedAudioFrame(new EncodedAudioFrame(buffer, 1024, 3), trackId: 2, pts: 3072, configVersion: 1);
            buffer[0] = 0xFF;

            packet.TrackId.Should().Be(2);
            packet.Pts.Should().Be(3072);
            packet.Dts.Should().Be(3072);
            packet.IsKeyframe.Should().BeTrue();
            packet.ConfigVersion.Should().Be(1);
            packet.Payload.ToArray().Should().Equal(0x21, 0x10, 0x05);
        }

        [Fact]
        public void MediaPacketFactory_FromAacStreamingEncoder_Should_Produce_Consecutive_Packets()
        {
            var packets = new List<MediaPacket>();
            var pts = 0L;
            using var encoder = EggEncoder.Streaming.Aac.AacStreamingEncoder.Create(1, 44100, frame =>
            {
                packets.Add(MediaPacketFactory.FromEncodedAudioFrame(frame, trackId: 0, pts));
                pts += frame.SampleCount;
            });

            encoder.Write(new int[3000]);
            encoder.Complete();

            packets.Should().NotBeEmpty();
            packets.Select(p => p.Pts).Should().BeInAscendingOrder();
            packets[1].Pts.Should().Be(1024);
            packets.Should().OnlyContain(p => p.IsKeyframe && p.Payload.Length > 0);
        }

        [Fact]
        public void MediaPacketFactory_InvalidArguments_Should_Throw()
        {
            var negativeTrack = () => MediaPacketFactory.FromEncodedAudioFrame(new EncodedAudioFrame([1], 1, 0), -1, 0);
            var negativeVersion = () => MediaPacketFactory.FromEncodedAudioFrame(new EncodedAudioFrame([1], 1, 0), 0, 0, -1);

            negativeTrack.Should().Throw<ArgumentOutOfRangeException>();
            negativeVersion.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
