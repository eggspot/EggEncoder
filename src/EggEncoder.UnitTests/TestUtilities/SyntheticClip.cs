using EggEncoder.Codecs.H264;
using EggEncoder.Codecs.Mov;
using EggEncoder.Containers.Mp4;
using EggEncoder.Media;
using EggEncoder.Streaming.Aac;
using EggEncoder.Video;

namespace EggEncoder.UnitTests.TestUtilities;

public static class SyntheticClip
{
    public static void Write(string path, int width, int height, int frameCount, int framesPerSecond, YuvColor color, double toneHz = 0, double toneAmplitude = 0.3)
    {
        using var file = File.Create(path);
        using var encoder = new H264Encoder(new H264EncoderSettings
        {
            Width = width,
            Height = height,
            FrameRate = new VideoFrameRate(framesPerSecond),
            Profile = H264Profile.Main,
            ConstantRateFactor = 18,
            ColorRange = VideoColorRange.Limited,
            ColorMatrix = VideoColorMatrix.Bt709
        });
        var tracks = new List<MediaTrackInfo>
        {
            new(1, MediaKind.Video, MediaCodec.H264, framesPerSecond * 512, encoder.DecoderConfiguration.ToArray(), 0, width: width, height: height)
        };
        if (toneHz > 0)
        {
            tracks.Add(new MediaTrackInfo(2, MediaKind.Audio, MediaCodec.Aac, 48000, Mp4EsdsParser.BuildAudioSpecificConfig(2, 48000), 0, sampleRate: 48000, channels: 2));
        }

        using var writer = new Mp4Writer(file, leaveOpen: true, new MemoryStream());
        writer.Open(new MediaTrackSet(tracks));
        if (toneHz > 0)
        {
            long written = 0;
            using var aac = AacStreamingEncoder.Create(2, 48000, 128_000, frame =>
            {
                var pts = written - AacStreamingEncoder.EncoderDelaySamples;
                writer.Write(new MediaPacket(2, pts, pts, true, 0, frame.Payload.ToArray()));
                written += frame.SampleCount;
            });
            var samples = (int)(48000L * frameCount / framesPerSecond);
            var pcm = new int[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                var value = (int)Math.Round(32767 * toneAmplitude * Math.Sin(2 * Math.PI * toneHz * i / 48000));
                pcm[2 * i] = value;
                pcm[(2 * i) + 1] = value;
            }

            aac.Write(pcm);
            aac.Complete();
        }

        using var frame = VideoFrame.Create(width, height, VideoColorRange.Limited, VideoColorMatrix.Bt709);
        for (var index = 0; index < frameCount; index++)
        {
            frame.Fill(color);
            frame.GetRow(VideoPlane.Y, (index * 3) % height)[..Math.Min(8, width)].Fill(235);
            frame.Timestamp = TimeSpan.FromSeconds(index / (double)framesPerSecond);
            var pts = index * 512L;
            encoder.Encode(frame, encoded => writer.Write(new MediaPacket(1, pts, pts, encoded.IsKeyframe, 0, encoded.Payload)));
        }

        writer.Complete();
    }

    public static void WriteTone(string path, double seconds, double toneHz, double amplitude = 0.3)
    {
        var samples = (int)(48000 * seconds);
        using var writer = EggEncoder.Codecs.Wav.WavWriter.Create(path, 2, 48000, 16, samples);
        var block = new int[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (int)Math.Round(32767 * amplitude * Math.Sin(2 * Math.PI * toneHz * i / 48000));
            block[2 * i] = value;
            block[(2 * i) + 1] = value;
        }

        writer.WriteInterleavedSamples(block, samples);
        writer.Finish();
    }
}
