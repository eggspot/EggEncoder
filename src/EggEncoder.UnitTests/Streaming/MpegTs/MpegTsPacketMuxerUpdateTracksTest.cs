using EggEncoder.Media;
using EggEncoder.Streaming.MpegTs;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
using FluentAssertions;
using static EggEncoder.UnitTests.Streaming.MpegTs.MpegTsTestReader;

namespace EggEncoder.UnitTests.Streaming.MpegTs
{
    public class MpegTsPacketMuxerUpdateTracksTest
    {
        private const int VideoPid = 0x100;
        private const int AudioPid = 0x101;

        [Fact]
        public void UpdateTracks_OnAFrameMuxer_Should_Throw()
        {
            var muxer = new MpegTsMuxer(1, 44100);

            var act = () => muxer.UpdateTracks(Tracks(0, 0));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void UpdateTracks_Null_Should_Throw()
        {
            var muxer = new MpegTsMuxer(Tracks(0, 0));

            var act = () => muxer.UpdateTracks(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void UpdateTracks_ChangedLayout_Should_Throw_And_LeaveTheMuxerUnchanged()
        {
            var muxer = new MpegTsMuxer(Tracks(0, 0));

            var act = () => muxer.UpdateTracks(new MediaTrackSet([AudioTrack(1), VideoTrack(1)]));

            act.Should().ThrowExactly<ArgumentException>();
            muxer.Invoking(m => m.BuildSegment([Video(0, 0)])).Should().NotThrow();
        }

        [Fact]
        public void UpdateTracks_ConfigurationAdtsCannotCarry_Should_Throw_And_LeaveTheMuxerUnchanged()
        {
            var muxer = new MpegTsMuxer(Tracks(0, 0));

            var act = () => muxer.UpdateTracks(new MediaTrackSet([VideoTrack(0), AudioTrack(1) with { CodecConfiguration = Convert.FromHexString("1240") }]));

            act.Should().ThrowExactly<NotSupportedException>();
            muxer.Invoking(m => m.BuildSegment([Audio(0, 0)])).Should().NotThrow();
        }

        [Fact]
        public void UpdateTracks_InvalidVideoConfiguration_Should_Throw_ArgumentException()
        {
            var muxer = new MpegTsMuxer(Tracks(0, 0));

            var act = () => muxer.UpdateTracks(new MediaTrackSet([VideoTrack(1) with { CodecConfiguration = new byte[] { 0x01 } }, AudioTrack(0)]));

            act.Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void UpdateTracks_Should_UseTheNewConfiguration_And_ContinueEveryCounterAndTimestamp()
        {
            var muxer = new MpegTsMuxer(Tracks(0, 0));
            var first = ReadPackets(muxer.BuildSegment([Video(0, 0), Audio(10, 0)]));
            var updated = new MediaTrackSet([
                VideoTrack(1) with { CodecConfiguration = MediaFixtures.AvcBaselineRecord },
                AudioTrack(1) with { CodecConfiguration = Convert.FromHexString("1190"), SampleRate = 48000, Channels = 2 }]);

            muxer.UpdateTracks(updated);

            muxer.Invoking(m => m.BuildSegment([Video(1000, 0)])).Should().ThrowExactly<ArgumentException>("the old version");
            muxer.Invoking(m => m.BuildSegment([Video(0, 1)])).Should().ThrowExactly<ArgumentException>("DTS must keep increasing");
            var second = ReadPackets(muxer.BuildSegment([Video(1000, 1), Audio(1010, 1)]));

            ReadPmt(second[1]).Should().BeEquivalentTo(ReadPmt(first[1]), "PIDs and stream types don't change");
            second[1].ContinuityCounter.Should().Be((first[1].ContinuityCounter + 1) & 0x0F);
            second.First(p => p.Pid == VideoPid && p.PayloadUnitStart).ContinuityCounter.Should().Be((first.Last(p => p.Pid == VideoPid).ContinuityCounter + 1) & 0x0F);

            var keyframe = ReadPes(second, VideoPid).Single().Es;
            var sps = AvcDecoderConfigurationRecord.Parse(MediaFixtures.AvcBaselineRecord).SequenceParameterSets[0].ToArray();
            byte[] annexBSps = [0, 0, 0, 1, .. sps];
            keyframe.AsSpan().IndexOf(annexBSps).Should().BePositive();

            var adts = ReadPes(second, AudioPid).Single().Es;
            ((adts[2] >> 2) & 0x0F).Should().Be(3, "48 kHz");
            (((adts[2] & 0x01) << 2) | (adts[3] >> 6)).Should().Be(2, "stereo");
        }

        [Fact]
        public void UpdateTracks_AudioOnly_Should_KeepThePcrOnTheSameTrack()
        {
            var muxer = new MpegTsMuxer(new MediaTrackSet([AudioTrack(0)]));
            muxer.BuildSegment([new MediaPacket(1, 0, 0, true, 0, new byte[] { 0x21 })]);

            muxer.UpdateTracks(new MediaTrackSet([AudioTrack(1)]));

            var packets = ReadPackets(muxer.BuildSegment([new MediaPacket(1, 1000, 1000, true, 1, new byte[] { 0x21 })]));
            ReadPmt(packets[1]).PcrPid.Should().Be(VideoPid, "the only track sits at the first elementary PID");
            ReadPes(packets, VideoPid).Single().First.Pcr.Should().NotBeNull();
        }

        private static MediaTrackSet Tracks(int videoVersion, int audioVersion) => new([VideoTrack(videoVersion), AudioTrack(audioVersion)]);

        private static MediaTrackInfo VideoTrack(int version) =>
            new(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, version, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack(int version) =>
            new(1, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, version, 44100, 1);

        private static MediaPacket Video(long dts, int version) =>
            new(0, dts, dts, true, version, new byte[] { 0, 0, 0, 2, 0x65, 0x80 });

        private static MediaPacket Audio(long dts, int version) => new(1, dts, dts, true, version, new byte[] { 0x21, 0x10 });
    }
}
