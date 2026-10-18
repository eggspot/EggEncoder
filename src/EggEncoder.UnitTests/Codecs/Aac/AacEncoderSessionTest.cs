using EggEncoder.Codecs.Aac;
using EggEncoder.UnitTests.TestUtilities;
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

        [Fact]
        public void OpenSession_Stream_WithNullStream_Should_Throw()
        {
            var act = () => AacEncoderSession.OpenSession((Stream)null!, channels: 1, sampleRate: 44100);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void OpenSession_Stream_WithUnsupportedSampleRate_Should_Throw()
        {
            using var destStream = new MemoryStream();

            var act = () => AacEncoderSession.OpenSession(destStream, channels: 1, sampleRate: 12345);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void OpenSession_Stream_WithStereoChannels_Should_Throw()
        {
            using var destStream = new MemoryStream();

            var act = () => AacEncoderSession.OpenSession(destStream, channels: 2, sampleRate: 44100);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void OpenSession_Stream_Should_RoundTrip_Exactly_SameAs_PathBased()
        {
            const int sampleRate = 44100;

            var buffer = new int[2000];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (short)(1000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var pathBasedOutput = Path.Combine(Path.GetTempPath(), $"aac_path_{Guid.NewGuid():N}.aac");
            try
            {
                using (var pathSession = AacEncoderSession.OpenSession(pathBasedOutput, channels: 1, sampleRate))
                {
                    pathSession.WriteInterleavedSamples(buffer, buffer.Length);
                    pathSession.Finish();
                }

                var pathBasedBytes = File.ReadAllBytes(pathBasedOutput);

                using var destStream = new MemoryStream();
                using (var streamSession = AacEncoderSession.OpenSession(destStream, channels: 1, sampleRate))
                {
                    streamSession.WriteInterleavedSamples(buffer, buffer.Length);
                    streamSession.Finish();
                }

                destStream.ToArray().Should().Equal(pathBasedBytes);
            }
            finally
            {
                File.Delete(pathBasedOutput);
            }
        }

        [Fact]
        public void OpenSession_Stream_WithNonSeekableDestination_Should_StillWork()
        {
            // Unlike FlacEncoder.OpenSession(Stream, ...), AacEncoderSession needs no CanSeek at
            // all -- confirmed directly by actually writing through a destination that throws on
            // any Seek/Position access, not just by the absence of a CanSeek check in the source.
            const int sampleRate = 44100;
            var samples = new[] { 100, -200, 300, -400, 500 };

            using var destStream = new NonSeekableWriteStream();
            using (var session = AacEncoderSession.OpenSession(destStream, channels: 1, sampleRate))
            {
                session.WriteInterleavedSamples(samples, samples.Length);
                session.Finish();
            }
        }

        [Fact]
        public void OpenSession_Stream_Dispose_Should_NotDisposeCallerStream()
        {
            using var destStream = new MemoryStream();

            using (var session = AacEncoderSession.OpenSession(destStream, channels: 1, sampleRate: 44100))
            {
                session.WriteInterleavedSamples([1, 2, 3], 3);
                session.Finish();
            }

            var act = () => destStream.Position;

            act.Should().NotThrow("the Stream-based overload must not dispose a stream the caller opened and owns, unlike the path-based overload's own FileStream");
        }
    }
}
