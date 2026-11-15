using EggEncoder.Codecs.H264;
using EggEncoder.Video;
using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264ConformanceTest
    {
        [Fact]
        public void Local_Conformance_Streams_Should_Decode_To_Their_Reference_Yuv()
        {
            var directory = Environment.GetEnvironmentVariable("EGGENCODER_H264_CONFORMANCE");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var failures = new List<string>();
            foreach (var stream in Directory.EnumerateFiles(directory).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".264" or ".h264" or ".jsv" or ".avc" or ".26l" or ".jvt"))
            {
                var reference = Path.ChangeExtension(stream, ".yuv");
                if (!File.Exists(reference))
                {
                    continue;
                }

                try
                {
                    var decoded = new MemoryStream();
                    using var decoder = new H264Decoder();
                    void Collect(VideoFrame frame)
                    {
                        for (var y = 0; y < frame.Height; y++)
                        {
                            decoded.Write(frame.GetRow(VideoPlane.Y, y));
                        }

                        foreach (var plane in new[] { VideoPlane.U, VideoPlane.V })
                        {
                            for (var y = 0; y < frame.ChromaHeight; y++)
                            {
                                decoded.Write(frame.GetRow(plane, y));
                            }
                        }

                        frame.Dispose();
                    }

                    foreach (var unit in AccessUnits(File.ReadAllBytes(stream)))
                    {
                        decoder.Decode(unit, TimeSpan.Zero, Collect);
                    }

                    decoder.Flush(Collect);
                    if (!decoded.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(reference)))
                    {
                        failures.Add($"{Path.GetFileName(stream)}: output differs from the reference");
                    }
                }
                catch (NotSupportedException)
                {
                }
            }

            failures.Should().BeEmpty();
        }
    }
}
