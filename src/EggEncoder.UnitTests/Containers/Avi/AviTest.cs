using System.Diagnostics;
using EggEncoder.Containers.Avi;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Containers.Avi
{
    public class AviTest
    {
        private static string Fixture(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "Containers", folder, name);

        [Fact]
        public void H264_Mp3_Avi_Should_Match_Ffprobe()
        {
            using var source = AviFileSource.Open(Fixture("Avi", "h264_mp3.avi"));
            var packets = source.ReadPackets().ToList();

            source.SkippedTrackCount.Should().Be(0);
            source.FrameSize.Should().Be((64, 48));
            var video = source.Tracks.FindTrack(0)!;
            (video.Codec, video.Width, video.Height, video.Timescale).Should().Be((MediaCodec.H264, 64, 48, 10));
            var audio = source.Tracks.FindTrack(1)!;
            (audio.Codec, audio.SampleRate, audio.Channels, audio.Timescale).Should().Be((MediaCodec.Mp3, 44100, 1, 1225));
            var videoPackets = packets.Where(p => p.TrackId == 0).ToList();
            videoPackets.Should().HaveCount(10);
            videoPackets.Count(p => p.IsKeyframe).Should().Be(2);
            videoPackets.Take(3).Select(p => p.Dts).Should().Equal(0, 2, 3);
            videoPackets.Should().OnlyContain(p => p.Pts == p.Dts);
            var audioPackets = packets.Where(p => p.TrackId == 1).ToList();
            audioPackets.Should().HaveCount(40);
            audioPackets.Take(3).Select(p => p.Pts).Should().Equal(0, 32, 64);
        }

        [Fact]
        public void H264_Copied_From_Mp4_Should_Keep_Its_Payloads()
        {
            using var avi = AviFileSource.Open(Fixture("Avi", "h264_aac.avi"));
            using var mp4 = Mp4FileSource.Open(Fixture("Mp4", "h264_aac_rotated.mp4"));
            var mp4Video = mp4.Tracks.Tracks.First(t => t.Kind == MediaKind.Video).TrackId;

            var fromAvi = avi.ReadPackets().Where(p => p.TrackId == 0).Select(p => Convert.ToHexString(p.Payload.Span)).ToList();
            var fromMp4 = mp4.ReadPackets().Where(p => p.TrackId == mp4Video).Select(p => Convert.ToHexString(p.Payload.Span)).ToList();

            avi.Tracks.FindTrack(1)!.Codec.Should().Be(MediaCodec.Aac);
            fromAvi.Should().Equal(fromMp4);
        }

        [Fact]
        public void Rewriting_An_Avi_Should_Give_The_Same_Packets_Back()
        {
            using var original = AviFileSource.Open(Fixture("Avi", "h264_mp3.avi"));
            var packets = original.ReadPackets().ToList();

            using var reread = new AviFileSource(new MemoryStream(Write(original.Tracks, packets)));
            var rereadPackets = reread.ReadPackets().ToList();

            reread.Tracks.Tracks.Select(t => (t.Codec, t.Timescale, t.Width, t.Height, t.SampleRate, t.Channels))
                .Should().Equal(original.Tracks.Tracks.Select(t => (t.Codec, t.Timescale, t.Width, t.Height, t.SampleRate, t.Channels)));
            foreach (var track in original.Tracks.Tracks)
            {
                rereadPackets.Where(p => p.TrackId == track.TrackId).Select(p => (p.Pts, p.IsKeyframe, Strip(p)))
                    .Should().Equal(packets.Where(p => p.TrackId == track.TrackId).Select(p => (p.Pts, p.IsKeyframe, Strip(p))));
            }
        }

        [Fact]
        public void An_Mp4_Remuxed_To_Avi_Should_Keep_Its_Frames()
        {
            using var mp4 = Mp4FileSource.Open(Fixture("Mp4", "h264_aac_rotated.mp4"));
            var packets = mp4.ReadPackets().ToList();

            using var avi = new AviFileSource(new MemoryStream(Write(mp4.Tracks, packets)));
            var reread = avi.ReadPackets().ToList();

            for (var i = 0; i < mp4.Tracks.Tracks.Count; i++)
            {
                var track = mp4.Tracks.Tracks[i];
                reread.Where(p => p.TrackId == i).Select(Strip)
                    .Should().Equal(packets.Where(p => p.TrackId == track.TrackId).Select(Strip));
            }
        }

        [Fact]
        public void Unsupported_Codecs_Should_Be_Refused()
        {
            var tracks = new MediaTrackSet([new MediaTrackInfo(0, MediaKind.Audio, MediaCodec.Opus, 48000, new byte[19], 0, 48000, 2)]);
            using var writer = new AviWriter(new MemoryStream());

            writer.Invoking(w => w.Open(tracks)).Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Damaged_Files_Should_Only_Throw_InvalidData()
        {
            var original = File.ReadAllBytes(Fixture("Avi", "h264_mp3.avi"));
            var random = new Random(5);
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
                    using var source = new AviFileSource(new MemoryStream(bytes));
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
        public void Ffprobe_Should_Read_The_Written_File()
        {
            if (Environment.GetEnvironmentVariable("EGGENCODER_FFMPEG_ORACLE") != "1")
            {
                return;
            }

            using var source = AviFileSource.Open(Fixture("Avi", "h264_mp3.avi"));
            var packets = source.ReadPackets().ToList();
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-{Guid.NewGuid():N}.avi");
            try
            {
                File.WriteAllBytes(path, Write(source.Tracks, packets));
                var info = new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -f null -")
                {
                    RedirectStandardError = true
                };
                using var process = Process.Start(info)!;
                var errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                process.ExitCode.Should().Be(0, errors);
                errors.Should().BeEmpty();
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string Strip(MediaPacket packet)
        {
            var units = new List<string>();
            var span = packet.Payload.Span;
            var offset = 0;
            if (packet.Payload.Length < 5)
            {
                return Convert.ToHexString(span);
            }

            while (offset + 4 <= span.Length)
            {
                var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
                if (length <= 0 || length > span.Length - offset - 4)
                {
                    return Convert.ToHexString(span);
                }

                var type = span[offset + 4] & 0x1F;
                if (type is not (7 or 8 or 9))
                {
                    units.Add(Convert.ToHexString(span.Slice(offset + 4, length)));
                }

                offset += 4 + length;
            }

            return string.Join("|", units);
        }

        private static byte[] Write(MediaTrackSet tracks, IEnumerable<MediaPacket> packets)
        {
            var destination = new MemoryStream();
            using (var writer = new AviWriter(destination, leaveOpen: true, scratch: new MemoryStream()))
            {
                writer.Open(tracks);
                foreach (var packet in packets.OrderBy(p => MediaTimestamp.Rescale(p.Dts, tracks.FindTrack(p.TrackId)!.Timescale, 1_000_000)))
                {
                    writer.Write(packet);
                }

                writer.Complete();
            }

            return destination.ToArray();
        }
    }
}
