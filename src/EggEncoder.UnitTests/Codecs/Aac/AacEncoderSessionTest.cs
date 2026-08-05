using EggEncoder.Codecs.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacEncoderSessionTest
    {
        [Fact]
        public void WriteInterleavedSamples_PastOneFrame_Should_Flush_To_Disk_Before_Finish()
        {
            const int sampleRate = 44100;

            var outputPath = Path.Combine(Path.GetTempPath(), $"aac_streaming_{Guid.NewGuid():N}.aac");
            try
            {
                using var session = AacEncoderSession.OpenSession(outputPath, channels: 1, sampleRate: sampleRate);

                // A single AAC-LC frame covers 1024 samples; writing more than that must emit at
                // least one ADTS frame to disk immediately, without waiting for Finish().
                var buffer = new int[2000];
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = (short)(1000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                session.WriteInterleavedSamples(buffer, buffer.Length);

                session.BytesWrittenForTesting.Should().BeGreaterThan(0, "the session should write completed frames to the stream as they're encoded, not buffer the whole track until Finish()");
            }
            finally
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }

        [Fact]
        public void OpenSession_WithUnsupportedSampleRate_Should_Throw_Immediately()
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"aac_invalid_{Guid.NewGuid():N}.aac");

            var act = () => AacEncoderSession.OpenSession(outputPath, channels: 1, sampleRate: 12345);

            act.Should().ThrowExactly<NotSupportedException>();
            File.Exists(outputPath).Should().BeFalse("validation should fail before any file is created");
        }

        [Fact]
        public void OpenSession_WithStereoChannels_Should_Throw_Immediately()
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"aac_invalid_{Guid.NewGuid():N}.aac");

            var act = () => AacEncoderSession.OpenSession(outputPath, channels: 2, sampleRate: 44100);

            act.Should().ThrowExactly<NotSupportedException>();
            File.Exists(outputPath).Should().BeFalse("validation should fail before any file is created");
        }
    }
}
