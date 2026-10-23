using System.Buffers.Binary;
using EggEncoder.Codecs.Mov;
using EggEncoder.Media;
using EggEncoder.Streaming.Mp4;
using EggEncoder.UnitTests.Containers.Flv;
using EggEncoder.UnitTests.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Streaming.Mp4
{
    public class FmpPacketSegmentWriterUpdateTracksTest
    {
        [Fact]
        public void UpdateTracks_OnAFrameWriter_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter();

            var act = () => writer.UpdateTracks(Tracks(0));

            act.Should().ThrowExactly<InvalidOperationException>();
        }

        [Fact]
        public void UpdateTracks_Null_Should_Throw()
        {
            var writer = new FmpMediaSegmentWriter(Tracks(0));

            var act = () => writer.UpdateTracks(null!);

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        public static TheoryData<string, MediaTrackSet> RejectedSets => new()
        {
            { "a track added", new MediaTrackSet([VideoTrack(1), AudioTrack(), AudioTrack() with { TrackId = 2 }]) },
            { "tracks reordered", new MediaTrackSet([AudioTrack(), VideoTrack(1)]) },
            { "a track renumbered", new MediaTrackSet([VideoTrack(1) with { TrackId = 3 }, AudioTrack()]) },
            { "a codec changed", new MediaTrackSet([VideoTrack(1) with { Codec = MediaCodec.H265, CodecConfiguration = MediaFixtures.Hevc100x60Record }, AudioTrack()]) },
            { "a kind changed", new MediaTrackSet([VideoTrack(1), AudioTrack() with { Kind = MediaKind.Video }]) },
            { "a timescale changed", new MediaTrackSet([VideoTrack(1), AudioTrack() with { Timescale = 44100 }]) },
            { "new bytes under the same version", new MediaTrackSet([VideoTrack(0) with { CodecConfiguration = MediaFixtures.AvcBaselineRecord }, AudioTrack()]) },
            { "an invalid configuration", new MediaTrackSet([VideoTrack(1) with { CodecConfiguration = new byte[] { 0x01 } }, AudioTrack()]) }
        };

        [Theory]
        [MemberData(nameof(RejectedSets))]
        public void UpdateTracks_RejectedSet_Should_Throw_And_LeaveTheWriterUnchanged(string reason, MediaTrackSet tracks)
        {
            var writer = new FmpMediaSegmentWriter(Tracks(0));

            var act = () => writer.UpdateTracks(tracks);

            act.Should().ThrowExactly<ArgumentException>(reason);
            writer.Invoking(w => w.BuildMediaSegment([Video(0, version: 0)])).Should().NotThrow();
        }

        [Fact]
        public void UpdateTracks_Should_RequireTheNewVersion_And_CarryTheSequenceNumberAndDecodeTimes()
        {
            var writer = new FmpMediaSegmentWriter(Tracks(0));
            writer.BuildMediaSegment([Video(0, version: 0), Video(40, version: 0)]);

            writer.UpdateTracks(Tracks(1));

            writer.Invoking(w => w.BuildMediaSegment([Video(80, version: 0)])).Should().ThrowExactly<ArgumentException>("the old version");
            writer.Invoking(w => w.BuildMediaSegment([Video(40, version: 1)])).Should().ThrowExactly<ArgumentException>("DTS must keep increasing");
            var segment = writer.BuildMediaSegment([Video(80, version: 1)]);
            ReadSequenceNumber(segment).Should().Be(2);
            ReadFirstTrun(segment).Should().Be((80L, 40u), "the last duration carries over the change");
        }

        [Fact]
        public void UpdateTracks_NewSampleRate_Should_ChangeTheDefaultDurationOfAFirstAudioSample()
        {
            var tracks = new MediaTrackSet([new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Aac, 48000, FlvTestBuilder.AacConfig, 0, 48000, 1)]);
            var writer = new FmpMediaSegmentWriter(tracks);

            writer.UpdateTracks(new MediaTrackSet([tracks.Tracks[0] with { ConfigVersion = 1, SampleRate = 24000 }]));

            var segment = writer.BuildMediaSegment([new MediaPacket(0, 0, 0, true, 1, new byte[] { 0x21 })]);
            ReadFirstTrun(segment).Should().Be((0L, 2048u), "1024 samples at 24 kHz in a 48 kHz timescale");
        }

        private static MediaTrackSet Tracks(int videoVersion) => new([VideoTrack(videoVersion), AudioTrack()]);

        private static MediaTrackInfo VideoTrack(int version) =>
            new(0, MediaKind.Video, MediaCodec.H264, 1000, FlvTestBuilder.AvcC, version, width: 64, height: 64);

        private static MediaTrackInfo AudioTrack() =>
            new(1, MediaKind.Audio, MediaCodec.Aac, 1000, FlvTestBuilder.AacConfig, 0, 44100, 1);

        private static MediaPacket Video(long dts, int version) =>
            new(0, dts, dts, true, version, new byte[] { 0, 0, 0, 1, 0x65 });

        private static uint ReadSequenceNumber(byte[] segment)
        {
            using var stream = new MemoryStream(segment);
            var moof = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moof");
            var mfhd = MovAtomReader.FindAtom(stream, "mfhd", moof.ContentStart, moof.ContentEnd)!.Value;
            return BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan((int)mfhd.ContentStart + 4));
        }

        private static (long BaseMediaDecodeTime, uint FirstDuration) ReadFirstTrun(byte[] segment)
        {
            using var stream = new MemoryStream(segment);
            var moof = MovAtomReader.EnumerateAtoms(stream, 0, stream.Length).Single(a => a.Type == "moof");
            var traf = MovAtomReader.FindAtom(stream, "traf", moof.ContentStart, moof.ContentEnd)!.Value;
            var tfdt = MovAtomReader.FindAtom(stream, "tfdt", traf.ContentStart, traf.ContentEnd)!.Value;
            var trun = MovAtomReader.FindAtom(stream, "trun", traf.ContentStart, traf.ContentEnd)!.Value;
            return (
                (long)BinaryPrimitives.ReadUInt64BigEndian(segment.AsSpan((int)tfdt.ContentStart + 4)),
                BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan((int)trun.ContentStart + 12)));
        }
    }
}
