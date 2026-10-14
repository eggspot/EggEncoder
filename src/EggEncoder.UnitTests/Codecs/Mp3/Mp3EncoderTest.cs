using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3EncoderTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");

        [Fact]
        public void Encode_Should_Produce_Valid_Decodable_Mp3_With_Correct_Format()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "output.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.DurationInSeconds.Should().Be(2);

                var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(mp3Path);
                streamInfo.Channels.Should().Be(2);
                samples.Should().NotBeEmpty();

                var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_WithLowerBitRate_Should_Produce_Smaller_File()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var highBitRatePath = Path.Combine(tempDirectory, "high.mp3");
                var lowBitRatePath = Path.Combine(tempDirectory, "low.mp3");

                Mp3Encoder.Encode(_wavFixturePath, highBitRatePath, bitRateKbps: 320);
                Mp3Encoder.Encode(_wavFixturePath, lowBitRatePath, bitRateKbps: 64);

                var highBitRateSize = new FileInfo(highBitRatePath).Length;
                var lowBitRateSize = new FileInfo(lowBitRatePath).Length;

                lowBitRateSize.Should().BeLessThan(highBitRateSize, $"expected 64kbps ({lowBitRateSize} bytes) to be smaller than 320kbps ({highBitRateSize} bytes)");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_WithTags_Should_Produce_Mp3_Whose_Id3v2Tag_RoundTrips_Through_Probe()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "tagged.mp3");
                var tags = new Dictionary<string, string> { ["TIT2"] = "Title", ["TPE1"] = "Artist" };

                Mp3Encoder.Encode(_wavFixturePath, mp3Path, tags: tags);

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.Tags.Should().NotBeNull();
                probeResult.Tags!.Should().ContainKey("TIT2").WhoseValue.Should().Be("Title");
                probeResult.Tags.Should().ContainKey("TPE1").WhoseValue.Should().Be("Artist");

                var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(mp3Path);
                streamInfo.Channels.Should().Be(2);
                samples.Should().NotBeEmpty();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_WithoutTags_Should_Produce_Mp3_With_NullTags()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "untagged.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.Tags.Should().BeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_Should_Produce_Mp3_With_EncoderDelay_Matching_TheKnownConstant()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "output.mp3");
                Mp3Encoder.Encode(_wavFixturePath, mp3Path);

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.EncoderDelaySamples.Should().Be(Mp3Encoder.EncoderDelaySamples);
                probeResult.EncoderPaddingSamples.Should().NotBeNull();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(1152 * 38)] // an exact multiple of one frame -- no natural partial-frame pad needed
        [InlineData((1152 * 38) + 37)] // not a multiple -- exercises the natural partial-frame pad path
        public void OpenSession_Should_Produce_Mp3_WithDuration_MatchingTheExactRealSampleCount(int totalSamples)
        {
            const int sampleRate = 44100;
            const int channels = 1;
            var rng = new Random(42);
            var samples = new int[totalSamples];
            for (var i = 0; i < totalSamples; i++)
            {
                samples[i] = (int)((rng.NextDouble() * 2.0 - 1.0) * 20000);
            }

            var mp3Path = Path.Combine(Path.GetTempPath(), $"mp3_duration_{Guid.NewGuid():N}.mp3");

            try
            {
                using (var session = Mp3Encoder.OpenSession(mp3Path, channels, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, totalSamples);
                    session.Finish();
                }

                var probeResult = Mp3Probe.Probe(mp3Path);
                probeResult.DurationSeconds.Should().BeApproximately((double)totalSamples / sampleRate, 1e-9);
            }
            finally
            {
                File.Delete(mp3Path);
            }
        }

        [Fact]
        public void OpenSession_WithInvalidTagId_Should_Throw_Before_Creating_DestFile()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var mp3Path = Path.Combine(tempDirectory, "invalid.mp3");
                var tags = new Dictionary<string, string> { ["BAD"] = "value" };

                var act = () => Mp3Encoder.OpenSession(mp3Path, channels: 2, sampleRate: 44100, bitsPerSample: 16, tags: tags);

                act.Should().Throw<ArgumentException>();
                File.Exists(mp3Path).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
