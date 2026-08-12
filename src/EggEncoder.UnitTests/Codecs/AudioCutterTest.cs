using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    public class AudioCutterTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");

        [Fact]
        public void Cut_Wav_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                using var wavReader = WavReader.Open(destPath);
                wavReader.Channels.Should().Be(2);
                wavReader.SampleRate.Should().Be(1000);
                wavReader.BitsPerSample.Should().Be(16);
                wavReader.TotalSamples.Should().Be(2000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                framesRead.Should().Be(2000);
                buffer.Should().Equal(interleavedSamples[2000..6000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Wav_With_StartBeyondDuration_Should_Return_False_And_Produce_No_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 10, endInSeconds: 20).Should().BeFalse();
                File.Exists(destPath).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Wav_With_EndBeyondDuration_Should_Clip_To_Available_Length()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 4, endInSeconds: 100).Should().BeTrue();

                using var wavReader = WavReader.Open(destPath);
                wavReader.TotalSamples.Should().Be(1000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples[8000..10000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Flac_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (wavPath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var sourceFlacPath = Path.Combine(tempDirectory, "source.flac");
                var destFlacPath = Path.Combine(tempDirectory, "cut.flac");

                FlacEncoder.Encode(wavPath, sourceFlacPath);
                AudioCutter.Cut(sourceFlacPath, destFlacPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(destFlacPath);

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(2000);
                decodedSamples.Should().Equal(interleavedSamples[2000..6000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Flac_With_StartBeyondDuration_Should_Return_False_And_Produce_No_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (wavPath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var sourceFlacPath = Path.Combine(tempDirectory, "source.flac");
                var destFlacPath = Path.Combine(tempDirectory, "cut.flac");

                FlacEncoder.Encode(wavPath, sourceFlacPath);
                AudioCutter.Cut(sourceFlacPath, destFlacPath, startInSeconds: 10, endInSeconds: 20).Should().BeFalse();
                File.Exists(destFlacPath).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Mp3_Should_Produce_Correct_Duration_And_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceMp3Path = Path.Combine(tempDirectory, "source.mp3");
                var cutMp3Path = Path.Combine(tempDirectory, "cut.mp3");

                Mp3Encoder.Encode(_wavFixturePath, sourceMp3Path);
                AudioCutter.Cut(sourceMp3Path, cutMp3Path, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                var probeResult = Mp3Probe.Probe(cutMp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.DurationInSeconds.Should().Be(1);

                var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(cutMp3Path);
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
        public void Cut_Mp3_With_StartBeyondDuration_Should_Return_False_And_Produce_No_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceMp3Path = Path.Combine(tempDirectory, "source.mp3");
                var cutMp3Path = Path.Combine(tempDirectory, "cut.mp3");

                Mp3Encoder.Encode(_wavFixturePath, sourceMp3Path);
                AudioCutter.Cut(sourceMp3Path, cutMp3Path, startInSeconds: 10, endInSeconds: 20).Should().BeFalse();
                File.Exists(cutMp3Path).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_UnsupportedExtension_Should_Throw()
        {
            var act = () => AudioCutter.Cut("source.ogg", "dest.ogg", 0, 10);
            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Cut_WavToMp3_CrossFormat_Should_Produce_Trimmed_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destMp3Path = Path.Combine(tempDirectory, "cut.mp3");

                AudioCutter.Cut(_wavFixturePath, destMp3Path, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                var probeResult = Mp3Probe.Probe(destMp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.DurationInSeconds.Should().Be(1);

                var (_, samples) = Mp3TestDecoder.DecodeAll(destMp3Path);
                var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToFlac_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var destFlacPath = Path.Combine(tempDirectory, "dest.flac");

                AudioCutter.Convert(sourcePath, destFlacPath);

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(destFlacPath);

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(1000);
                decodedSamples.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToMp3_Should_Produce_Correct_Duration_And_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destMp3Path = Path.Combine(tempDirectory, "dest.mp3");

                AudioCutter.Convert(_wavFixturePath, destMp3Path);

                var probeResult = Mp3Probe.Probe(destMp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.Channels.Should().Be(2);
                probeResult.DurationInSeconds.Should().Be(2);

                var (_, samples) = Mp3TestDecoder.DecodeAll(destMp3Path);
                var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_FlacToMp3_Should_Produce_Correct_Duration_And_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceFlacPath = Path.Combine(tempDirectory, "source.flac");
                var destMp3Path = Path.Combine(tempDirectory, "dest.mp3");

                FlacEncoder.Encode(_wavFixturePath, sourceFlacPath);
                AudioCutter.Convert(sourceFlacPath, destMp3Path);

                var probeResult = Mp3Probe.Probe(destMp3Path);
                probeResult.SampleRate.Should().Be(44100);
                probeResult.DurationInSeconds.Should().Be(2);

                var (_, samples) = Mp3TestDecoder.DecodeAll(destMp3Path);
                var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_Mp3ToFlac_Should_Produce_Correct_Duration_And_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceMp3Path = Path.Combine(tempDirectory, "source.mp3");
                var destFlacPath = Path.Combine(tempDirectory, "dest.flac");

                Mp3Encoder.Encode(_wavFixturePath, sourceMp3Path);
                AudioCutter.Convert(sourceMp3Path, destFlacPath);

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(destFlacPath);

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(44100);
                decodedSamples.Should().NotBeEmpty();

                var rootMeanSquare = Math.Sqrt(decodedSamples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToWma_Should_Produce_Correct_Duration_And_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWmaPath = Path.Combine(tempDirectory, "dest.wma");

                AudioCutter.Convert(_wavFixturePath, destWmaPath);

                var decodedSamples = new List<int>();
                var streamInfo = WmaDecoder.Decode(destWmaPath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(44100);
                decodedSamples.Should().NotBeEmpty();

                var rootMeanSquare = Math.Sqrt(decodedSamples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WavToWma_Should_Produce_Trimmed_NonSilent_Output()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWmaPath = Path.Combine(tempDirectory, "cut.wma");

                var wasCut = AudioCutter.Cut(_wavFixturePath, destWmaPath, startInSeconds: 0, endInSeconds: 1);

                wasCut.Should().BeTrue();

                var decodedSamples = new List<int>();
                var streamInfo = WmaDecoder.Decode(destWmaPath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(44100);
                decodedSamples.Should().NotBeEmpty();

                var rootMeanSquare = Math.Sqrt(decodedSamples.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_UnsupportedExtension_Should_Throw()
        {
            var act = () => AudioCutter.Convert("source.ogg", "dest.wav");
            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Convert_Mp4ToWav_Should_Produce_Correct_Audio()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceMp4Path = Path.Combine(tempDirectory, "source.mp4");
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");
                CreateAacMp4(sourceMp4Path, sampleRate: 44100, seconds: 1);

                AudioCutter.Convert(sourceMp4Path, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var rootMeanSquare = Math.Sqrt(buffer.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Mp4ToWav_Should_Extract_Trimmed_NonSilent_Audio()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceMp4Path = Path.Combine(tempDirectory, "source.mp4");
                var destWavPath = Path.Combine(tempDirectory, "cut.wav");
                CreateAacMp4(sourceMp4Path, sampleRate: 44100, seconds: 3);

                AudioCutter.Cut(sourceMp4Path, destWavPath, startInSeconds: 1, endInSeconds: 2).Should().BeTrue();

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.TotalSamples.Should().BeGreaterThan(0);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var rootMeanSquare = Math.Sqrt(buffer.Average(sample => (double)sample * sample));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static void CreateAacMp4(string destMp4Path, int sampleRate, int seconds)
        {
            var sampleCount = sampleRate * seconds;
            var samples = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var tempAacPath = destMp4Path + ".tmp.aac";
            try
            {
                AacEncoder.Encode(tempAacPath, samples, channels: 1, sampleRate);
                var rawFrames = Mp4FileBuilder.ExtractRawAacFrames(tempAacPath);
                Mp4FileBuilder.Create(destMp4Path, sampleRate, rawFrames);
            }
            finally
            {
                File.Delete(tempAacPath);
            }
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }

        private static (string FilePath, int[] InterleavedSamples) CreateRampWav(string tempDirectory, string fileName, int totalFrames, int sampleRate)
        {
            var filePath = Path.Combine(tempDirectory, fileName);
            var interleavedSamples = new int[totalFrames * 2];

            for (var frame = 0; frame < totalFrames; frame++)
            {
                interleavedSamples[frame * 2] = frame;
                interleavedSamples[(frame * 2) + 1] = -frame;
            }

            WavFileBuilder.Create(filePath, channels: 2, sampleRate, bitsPerSample: 16, interleavedSamples);

            return (filePath, interleavedSamples);
        }
    }
}
