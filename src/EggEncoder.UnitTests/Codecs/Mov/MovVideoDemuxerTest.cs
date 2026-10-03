using EggEncoder.Codecs.Mov;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mov
{
    public class MovVideoDemuxerTest
    {
        // Ground truth for both fixtures below was read directly off `ffprobe -select_streams v:0
        // -show_entries frame=pkt_pos,pkt_size,key_frame` / `-count_frames`: 125 samples, codec
        // 'avc1', exactly one keyframe at sample index 0, first sample offset=48/size=6162, last
        // sample size=60 -- the same ffmpeg-produced-fixture cross-check pattern
        // FlacFfmpegCrossCheckTest uses for audio.
        [Fact]
        public void DemuxVideoTrack_RealMp4Fixture_Should_Match_FfprobeGroundTruth()
        {
            var info = MovVideoDemuxer.DemuxVideoTrack(Path.GetFullPath("Codecs/Mov/test.mp4"));

            info.CodecFourCc.Should().Be("avc1");
            info.Samples.Should().HaveCount(125);

            info.Samples[0].Offset.Should().Be(48);
            info.Samples[0].Size.Should().Be(6162);
            info.Samples[0].IsKeyframe.Should().BeTrue();
            info.Samples[^1].Size.Should().Be(60);

            for (var i = 1; i < info.Samples.Count; i++)
            {
                info.Samples[i].IsKeyframe.Should().BeFalse($"sample {i} is not ffprobe's sole key_frame=1 entry");
            }
        }

        [Fact]
        public void DemuxVideoTrack_RealMovFixture_Should_Match_FfprobeGroundTruth()
        {
            var info = MovVideoDemuxer.DemuxVideoTrack(Path.GetFullPath("Codecs/Mov/test.mov"));

            info.CodecFourCc.Should().Be("avc1");
            info.Samples.Should().HaveCount(125);
            info.Samples[0].IsKeyframe.Should().BeTrue();
        }

        [Fact]
        public void DemuxVideoTrack_FileWithoutVideoTrack_Should_Throw()
        {
            // Mp4FileBuilder.Create produces an audio-only MP4 (no 'vide' handler track at all) --
            // the exact inverse of MovDecoderTest's own "file without an audio track" case, which
            // uses the real (video-only) test.mov fixture for that side.
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_no_video_{Guid.NewGuid():N}.mp4");
            try
            {
                Mp4FileBuilder.Create(filePath, sampleRate: 44100, rawAacFrames: [[1, 2, 3]]);

                var act = () => MovVideoDemuxer.DemuxVideoTrack(filePath);

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_WithNoStssBox_Should_Mark_Every_Sample_As_Keyframe()
        {
            // Per ISO/IEC 14496-12, a track with no 'stss' box at all has no non-random-access
            // samples -- every sample is implicitly a sync sample.
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_no_stss_{Guid.NewGuid():N}.mp4");
            try
            {
                var samples = new[] { new byte[] { 1 }, new byte[] { 2, 2 }, new byte[] { 3, 3, 3 } };
                Mp4FileBuilder.CreateVideoOnly(filePath, "avc1", samples, keyframeSampleIndices: null);

                var info = MovVideoDemuxer.DemuxVideoTrack(filePath);

                info.Samples.Should().HaveCount(3);
                info.Samples.Should().OnlyContain(s => s.IsKeyframe);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_WithEmptyStssBox_Should_Mark_No_Sample_As_Keyframe()
        {
            // An 'stss' box that IS present but declares zero entries is a distinct, valid state
            // from "no 'stss' box at all" -- no sample is ever a sync sample here.
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_empty_stss_{Guid.NewGuid():N}.mp4");
            try
            {
                var samples = new[] { new byte[] { 1 }, new byte[] { 2, 2 }, new byte[] { 3, 3, 3 } };
                Mp4FileBuilder.CreateVideoOnly(filePath, "avc1", samples, keyframeSampleIndices: []);

                var info = MovVideoDemuxer.DemuxVideoTrack(filePath);

                info.Samples.Should().HaveCount(3);
                info.Samples.Should().OnlyContain(s => !s.IsKeyframe);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_WithMultipleStssEntries_Should_Mark_Exactly_Those_Samples_As_Keyframe()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_multi_stss_{Guid.NewGuid():N}.mp4");
            try
            {
                var samples = new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, new byte[] { 5 } };
                Mp4FileBuilder.CreateVideoOnly(filePath, "avc1", samples, keyframeSampleIndices: [0, 2, 4]);

                var info = MovVideoDemuxer.DemuxVideoTrack(filePath);

                info.Samples.Select(s => s.IsKeyframe).Should().Equal(true, false, true, false, true);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_PreservesSampleOffsetsAndSizes()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_sizes_{Guid.NewGuid():N}.mp4");
            try
            {
                var samples = new[] { new byte[10], new byte[25], new byte[3] };
                Mp4FileBuilder.CreateVideoOnly(filePath, "avc1", samples, keyframeSampleIndices: [0]);

                var info = MovVideoDemuxer.DemuxVideoTrack(filePath);

                info.Samples.Select(s => s.Size).Should().Equal(10, 25, 3);
                info.Samples[1].Offset.Should().Be(info.Samples[0].Offset + 10);
                info.Samples[2].Offset.Should().Be(info.Samples[1].Offset + 25);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_PreservesCodecFourCc()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_fourcc_{Guid.NewGuid():N}.mp4");
            try
            {
                Mp4FileBuilder.CreateVideoOnly(filePath, "vp09", [new byte[] { 1 }], keyframeSampleIndices: [0]);

                var info = MovVideoDemuxer.DemuxVideoTrack(filePath);

                info.CodecFourCc.Should().Be("vp09");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_WithNoSampleDescriptionEntries_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_no_stsd_entries_{Guid.NewGuid():N}.mp4");
            try
            {
                Mp4FileBuilder.CreateVideoOnly(filePath, "avc1", [new byte[] { 1 }], keyframeSampleIndices: [0], sampleEntryCount: 0);

                var act = () => MovVideoDemuxer.DemuxVideoTrack(filePath);

                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*stsd*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void DemuxVideoTrack_WithMissingMoovAtom_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"mov_video_demuxer_no_moov_{Guid.NewGuid():N}.mp4");
            try
            {
                File.WriteAllBytes(filePath, "not a valid mov file"u8.ToArray());

                var act = () => MovVideoDemuxer.DemuxVideoTrack(filePath);

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
