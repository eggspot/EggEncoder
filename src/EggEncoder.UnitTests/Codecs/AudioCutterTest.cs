using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Au;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Codecs.Wma;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    public class AudioCutterTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/Flac/sample.wav");
        private static readonly string _imaAdpcmMonoFixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono.wav");
        private static readonly string _imaAdpcmMonoExpectedPcmPath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono_expected.pcm");
        private static readonly string _msAdpcmMonoFixturePath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav");
        private static readonly string _msAdpcmMonoExpectedPcmPath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono_expected.pcm");
        private static readonly string _muLawMonoFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono.wav");
        private static readonly string _muLawMonoExpectedPcmPath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono_expected.pcm");
        private static readonly string _aLawMonoFixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_alaw_mono.wav");
        private static readonly string _aLawMonoExpectedPcmPath = Path.GetFullPath("Codecs/Wav/sample_g711_alaw_mono_expected.pcm");
        private static readonly string _ima4MonoFixturePath = Path.GetFullPath("Codecs/Aiff/fixture_ima4_mono.aifc");
        private static readonly string _ima4MonoExpectedPcmPath = Path.GetFullPath("Codecs/Aiff/fixture_ima4_mono_expected.pcm");

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
            var act = () => AudioCutter.Cut("source.xyz", "dest.xyz", 0, 10);
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
        public void Cut_Aiff_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampAiff(tempDirectory, "source.aiff", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.aiff");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                using var aiffReader = AiffReader.Open(destPath);
                aiffReader.Channels.Should().Be(2);
                aiffReader.SampleRate.Should().Be(1000);
                aiffReader.BitsPerSample.Should().Be(16);
                aiffReader.TotalSamples.Should().Be(2000);

                var buffer = new int[aiffReader.TotalSamples * aiffReader.Channels];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples);

                framesRead.Should().Be(2000);
                buffer.Should().Equal(interleavedSamples[2000..6000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_AiffToWav_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampAiff(tempDirectory, "source.aiff", totalFrames: 2000, sampleRate: 1000);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(2);
                wavReader.SampleRate.Should().Be(1000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToAiff_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var destAiffPath = Path.Combine(tempDirectory, "dest.aiff");

                AudioCutter.Convert(sourcePath, destAiffPath);

                using var aiffReader = AiffReader.Open(destAiffPath);
                aiffReader.Channels.Should().Be(2);
                aiffReader.SampleRate.Should().Be(1000);

                var buffer = new int[aiffReader.TotalSamples * aiffReader.Channels];
                aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_AifExtension_Should_Be_Treated_The_Same_As_Aiff()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampAiff(tempDirectory, "source.aif", totalFrames: 100, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.aif");

                AudioCutter.Convert(sourcePath, destPath);

                using var aiffReader = AiffReader.Open(destPath);
                var buffer = new int[aiffReader.TotalSamples * aiffReader.Channels];
                aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_AifcExtension_Should_Be_Treated_The_Same_As_Aiff()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampAiff(tempDirectory, "source.aifc", totalFrames: 100, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.aifc");

                AudioCutter.Convert(sourcePath, destPath);

                using var aiffReader = AiffReader.Open(destPath);
                var buffer = new int[aiffReader.TotalSamples * aiffReader.Channels];
                aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_RealAifcFixture_Should_Decode_BitExact_Samples()
        {
            // Decode-only direction against a real, ffmpeg-produced AIFC fixture (not one this project's
            // own AiffWriter helped produce) -- CreateRampAiff above already covers plain-AIFF-shaped
            // self-consistency; this is the independent cross-check for AIFC specifically, mirroring
            // every other codec's own real-fixture convention in this test suite.
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourcePath = Path.GetFullPath("Codecs/Aiff/fixture_fl32_mono.aifc");
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath);

                using var wavReader = WavReader.Open(destPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.BitsPerSample.Should().Be(32);
                wavReader.TotalSamples.Should().Be(4410);

                using var aiffReader = AiffReader.Open(sourcePath);
                var expected = new int[aiffReader.TotalSamples];
                aiffReader.ReadInterleavedSamples(expected, (int)aiffReader.TotalSamples);

                var buffer = new int[wavReader.TotalSamples];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithAifcDestinationFormat_Should_Write_RequestedCompressionType()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampAiff(tempDirectory, "source.aiff", totalFrames: 100, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.aifc");

                AudioCutter.Convert(sourcePath, destPath, AiffSampleFormat.LittleEndianInteger);

                using var aiffReader = AiffReader.Open(destPath);
                aiffReader.IsLittleEndian.Should().BeTrue();

                var buffer = new int[aiffReader.TotalSamples * aiffReader.Channels];
                aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithAifcDestinationFormat_ToNonAiffExtension_Should_Throw()
        {
            // .flac (not .wav): OpenSinkForPipeline has its own dedicated .wav branch that calls
            // WavWriter.Create directly without ever consulting destinationAiffFormat at all --
            // exactly mirroring how that same branch never consults destinationWavFormat for an
            // .aiff/.aifc destination either, a pre-existing characteristic of the pipeline-aware
            // sink-opening path, not something this feature changes. Only a destination extension
            // with no dedicated branch (falling through to the generic OpenSink, which is where the
            // actual validation lives) reaches this check -- the same reason the analogous WAV test
            // (Convert_WithG711Destination_ToNonWavExtension_Should_Throw) also targets .flac.
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, _) = CreateRampAiff(tempDirectory, "source.aiff", totalFrames: 10, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                var act = () => AudioCutter.Convert(sourcePath, destPath, AiffSampleFormat.Float32);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_RealAuFixture_Should_Decode_BitExact_Samples()
        {
            // Decode-only direction against a real, ffmpeg-produced AU fixture -- mirrors
            // Convert_RealAifcFixture_Should_Decode_BitExact_Samples's own role for AIFC.
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourcePath = Path.GetFullPath("Codecs/Au/fixture_s24be_mono.au");
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath);

                using var wavReader = WavReader.Open(destPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.BitsPerSample.Should().Be(24);
                wavReader.TotalSamples.Should().Be(4410);

                using var auReader = AuReader.Open(sourcePath);
                var expected = new int[auReader.TotalSamples];
                auReader.ReadInterleavedSamples(expected, (int)auReader.TotalSamples);

                var buffer = new int[wavReader.TotalSamples];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToAu_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 8000);
                var destAuPath = Path.Combine(tempDirectory, "dest.au");

                AudioCutter.Convert(sourcePath, destAuPath);

                using var auReader = AuReader.Open(destAuPath);
                auReader.Channels.Should().Be(2);
                auReader.SampleRate.Should().Be(8000);
                auReader.TotalSamples.Should().Be(2000);

                var buffer = new int[auReader.TotalSamples * auReader.Channels];
                auReader.ReadInterleavedSamples(buffer, (int)auReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithAuDestinationFormat_Should_Write_RequestedEncoding()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.au");

                AudioCutter.Convert(sourcePath, destPath, AuSampleFormat.MuLaw);

                using var auReader = AuReader.Open(destPath);
                auReader.IsMuLaw.Should().BeTrue();
                auReader.BitsPerSample.Should().Be(16);

                var buffer = new int[auReader.TotalSamples * auReader.Channels];
                auReader.ReadInterleavedSamples(buffer, (int)auReader.TotalSamples);

                var expected = interleavedSamples.Select(s =>
                    EggEncoder.Codecs.Wav.G711Codec.DecodeMuLaw(EggEncoder.Codecs.Wav.G711Codec.EncodeMuLaw(s))).ToArray();
                buffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithAuDestinationFormat_ToNonAuExtension_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 10, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                var act = () => AudioCutter.Convert(sourcePath, destPath, AuSampleFormat.Float32);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Caf_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampCaf(tempDirectory, "source.caf", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.caf");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.BitsPerSample.Should().Be(16);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples[1000..3000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_CafToWav_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampCaf(tempDirectory, "source.caf", totalFrames: 2000, sampleRate: 1000);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(1000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToCaf_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var filePath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new int[2000];
                for (var frame = 0; frame < interleavedSamples.Length; frame++)
                {
                    interleavedSamples[frame] = frame % 1000;
                }

                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples);
                var destCafPath = Path.Combine(tempDirectory, "dest.caf");

                AudioCutter.Convert(filePath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToCaf_Stereo_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var destCafPath = Path.Combine(tempDirectory, "dest.caf");

                AudioCutter.Convert(sourcePath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToCaf_TwentyFourBit_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var filePath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new[] { 0, 8_388_607, -8_388_608, 1_000_000, -1_000_000, 42 };

                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 24, interleavedSamples);
                var destCafPath = Path.Combine(tempDirectory, "dest.caf");

                AudioCutter.Convert(filePath, destCafPath);

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destCafPath, (block, _, _, bitsPerSample, _) =>
                {
                    bitsPerSample.Should().Be(24);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(1);
                streamInfo.BitsPerSample.Should().Be(24);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Caf_Stereo_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var sourceCafPath = Path.Combine(tempDirectory, "source.caf");
                AudioCutter.Convert(sourcePath, sourceCafPath);
                var destPath = Path.Combine(tempDirectory, "cut.caf");

                AudioCutter.Cut(sourceCafPath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples[2000..6000]);
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
            var act = () => AudioCutter.Convert("source.xyz", "dest.wav");
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

        private static (string FilePath, int[] InterleavedSamples) CreateRampAiff(string tempDirectory, string fileName, int totalFrames, int sampleRate)
        {
            var filePath = Path.Combine(tempDirectory, fileName);
            var interleavedSamples = new int[totalFrames * 2];

            for (var frame = 0; frame < totalFrames; frame++)
            {
                interleavedSamples[frame * 2] = frame;
                interleavedSamples[(frame * 2) + 1] = -frame;
            }

            AiffFileBuilder.Create(filePath, channels: 2, sampleRate, bitsPerSample: 16, interleavedSamples);

            return (filePath, interleavedSamples);
        }

        private static (string FilePath, int[] InterleavedSamples) CreateRampCaf(string tempDirectory, string fileName, int totalFrames, int sampleRate)
        {
            // ALAC in this codebase is mono-only (see AlacDecoder's doc comment), unlike the other
            // ramp helpers above which build stereo fixtures.
            var filePath = Path.Combine(tempDirectory, fileName);
            var interleavedSamples = new int[totalFrames];

            for (var frame = 0; frame < totalFrames; frame++)
            {
                interleavedSamples[frame] = frame % 1000;
            }

            using (var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate, bitsPerSample: 16))
            {
                session.WriteInterleavedSamples(interleavedSamples, totalFrames);
                session.Finish();
            }

            return (filePath, interleavedSamples);
        }

        [Fact]
        public void Cut_Tta_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampTta(tempDirectory, "source.tta", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "cut.tta");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.BitsPerSample.Should().Be(16);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples[1000..3000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_TtaToWav_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampTta(tempDirectory, "source.tta", totalFrames: 2000, sampleRate: 1000);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(1000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToTta_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var filePath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new int[2000];
                for (var frame = 0; frame < interleavedSamples.Length; frame++)
                {
                    interleavedSamples[frame] = frame % 1000;
                }

                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples);
                var destTtaPath = Path.Combine(tempDirectory, "dest.tta");

                AudioCutter.Convert(filePath, destTtaPath);

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destTtaPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToTta_Stereo_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 2000, sampleRate: 1000);
                var destTtaPath = Path.Combine(tempDirectory, "dest.tta");

                AudioCutter.Convert(sourcePath, destTtaPath);

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destTtaPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Tta_Stereo_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var (sourcePath, interleavedSamples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var sourceTtaPath = Path.Combine(tempDirectory, "source.tta");
                AudioCutter.Convert(sourcePath, sourceTtaPath);
                var destPath = Path.Combine(tempDirectory, "cut.tta");

                AudioCutter.Cut(sourceTtaPath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(2);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples[2000..6000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToOpus_Should_Produce_DecodableFile()
        {
            // Opus is lossy and fixed at 48kHz (see OpusEncoderSession's own doc comment) -- unlike
            // the lossless CAF/TTA conversions above, this only confirms the dispatch/plumbing
            // reaches OpusEncoderSession and produces a structurally valid, decodable file; actual
            // encode fidelity (signal-to-noise ratio) is already covered by OpusEncoderSessionTest.
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 48000;
                var samples = new int[sampleRate];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var destOpusPath = Path.Combine(tempDirectory, "dest.opus");

                AudioCutter.Convert(sourcePath, destOpusPath);

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(destOpusPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);
                decoded.Should().NotBeEmpty();
                decoded.Count.Should().BeGreaterThanOrEqualTo(samples.Length);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_OpusToWav_Should_Reproduce_RecognizableSignal()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 48000;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var sourceOpusPath = Path.Combine(tempDirectory, "source.opus");
                OpusEncoder.Encode(sourceWavPath, sourceOpusPath);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourceOpusPath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(sampleRate);

                var buffer = new int[wavReader.TotalSamples];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().NotBeEmpty();
                var rootMeanSquare = Math.Sqrt(buffer.Average(s => (double)s * s));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Opus_Should_Extract_ApproximateSampleRange()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 48000;
                var samples = new int[sampleRate * 5];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var sourceOpusPath = Path.Combine(tempDirectory, "source.opus");
                OpusEncoder.Encode(sourceWavPath, sourceOpusPath);
                var destPath = Path.Combine(tempDirectory, "cut.opus");

                AudioCutter.Cut(sourceOpusPath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                // Opus's fixed 960-sample frame size means a cut's exact boundary can't land on an
                // arbitrary sample the way a lossless codec's can -- approximately 2 seconds
                // (96000 samples), within a couple of frames' worth of slack either way.
                decoded.Count.Should().BeInRange(96000 - 2000, 96000 + 2000);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToOgg_Should_Produce_DecodableFile()
        {
            // Vorbis is lossy, like Opus above -- this confirms the dispatch/plumbing reaches
            // VorbisEncoderSession and produces a structurally valid, decodable file; actual encode
            // fidelity (signal-to-noise ratio) is already covered by VorbisEncoderSessionTest.
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                var samples = new int[sampleRate];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var destOggPath = Path.Combine(tempDirectory, "dest.ogg");

                AudioCutter.Convert(sourcePath, destOggPath);

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(destOggPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_OggToWav_Should_Reproduce_RecognizableSignal()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var sourceOggPath = Path.Combine(tempDirectory, "source.ogg");
                VorbisEncoder.Encode(sourceWavPath, sourceOggPath);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourceOggPath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(sampleRate);

                var buffer = new int[wavReader.TotalSamples];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().NotBeEmpty();
                var rootMeanSquare = Math.Sqrt(buffer.Average(s => (double)s * s));
                rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Vorbis_Should_Extract_ApproximateSampleRange()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                const int sampleRate = 44100;
                var samples = new int[sampleRate * 5];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var sourceOggPath = Path.Combine(tempDirectory, "source.ogg");
                VorbisEncoder.Encode(sourceWavPath, sourceOggPath);
                var destPath = Path.Combine(tempDirectory, "cut.ogg");

                AudioCutter.Cut(sourceOggPath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                // Vorbis has no fixed frame size the way Opus does, but its own encoder lookahead
                // (see VorbisEncoderSessionTest's remarks) means a cut's exact boundary still can't
                // land on an arbitrary sample the way a lossless codec's can -- approximately
                // 2 seconds (88200 samples), within generous slack either way.
                decoded.Count.Should().BeInRange(88200 - 8000, 88200 + 8000);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavToWavPack_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var filePath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new int[2000];
                for (var frame = 0; frame < interleavedSamples.Length; frame++)
                {
                    interleavedSamples[frame] = frame % 1000;
                }

                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples);
                var destWvPath = Path.Combine(tempDirectory, "dest.wv");

                AudioCutter.Convert(filePath, destWvPath);

                var decoded = new List<int>();
                var streamInfo = WavPackDecoder.Decode(destWvPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WavPackToWav_Should_Reproduce_Exact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new int[2000];
                for (var frame = 0; frame < interleavedSamples.Length; frame++)
                {
                    interleavedSamples[frame] = frame % 1000;
                }

                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples);
                var sourceWvPath = Path.Combine(tempDirectory, "source.wv");
                WavPackEncoder.Encode(sourceWavPath, sourceWvPath);
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourceWvPath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(1000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WavPack_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                var interleavedSamples = new int[5000];
                for (var frame = 0; frame < interleavedSamples.Length; frame++)
                {
                    interleavedSamples[frame] = frame % 1000;
                }

                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples);
                var sourceWvPath = Path.Combine(tempDirectory, "source.wv");
                WavPackEncoder.Encode(sourceWavPath, sourceWvPath);
                var destPath = Path.Combine(tempDirectory, "cut.wv");

                AudioCutter.Cut(sourceWvPath, destPath, startInSeconds: 1, endInSeconds: 3).Should().BeTrue();

                var decoded = new List<int>();
                var streamInfo = WavPackDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.BitsPerSample.Should().Be(16);
                streamInfo.TotalSamples.Should().Be(2000);
                decoded.Should().Equal(interleavedSamples[1000..3000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_ImaAdpcmWavToWav_Should_Reproduce_BitExact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(_imaAdpcmMonoFixturePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.TotalSamples.Should().Be(88200);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(ReadGroundTruthPcm16(_imaAdpcmMonoExpectedPcmPath));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_ImaAdpcmWav_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(_imaAdpcmMonoFixturePath, destWavPath, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.TotalSamples.Should().Be(44100);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var expected = ReadGroundTruthPcm16(_imaAdpcmMonoExpectedPcmPath);
                buffer.Should().Equal(expected[0..44100]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_Ima4AifcToWav_Should_Reproduce_BitExact_Samples()
        {
            // Decode-only direction against a real, ffmpeg-produced AIFC ima4 fixture -- mirrors the
            // depth already given to WAV's own IMA/MS ADPCM above, since ima4's block-oriented,
            // stateful decode is genuinely distinct machinery deserving its own integration coverage,
            // unlike AIFC's simpler per-sample compressionTypes (sowt/fl32/alaw/etc.) which just reuse
            // AiffReader's already-tested generic read path through this same Convert/Cut machinery.
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(_ima4MonoFixturePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.TotalSamples.Should().Be(8832);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(ReadGroundTruthPcm16(_ima4MonoExpectedPcmPath));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_Ima4Aifc_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "cut.wav");

                // The fixture is only ~0.2s long, so endInSeconds: 1 clamps to its own full 8832
                // samples (GetSampleRange's own Math.Clamp(..., startSample, totalSamples)) -- this
                // still exercises the Cut-specific code path (distinct from Convert's) with ima4 as
                // the source, just without a genuinely partial range to assert against.
                AudioCutter.Cut(_ima4MonoFixturePath, destWavPath, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.TotalSamples.Should().Be(8832);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(ReadGroundTruthPcm16(_ima4MonoExpectedPcmPath));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_MsAdpcmWavToWav_Should_Reproduce_BitExact_Samples()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(_msAdpcmMonoFixturePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(22050);
                wavReader.TotalSamples.Should().Be(44100);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var expected = ReadGroundTruthPcm16(_msAdpcmMonoExpectedPcmPath).Take((int)wavReader.TotalSamples).ToArray();
                buffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_MsAdpcmWav_Should_Extract_Exact_Sample_Range()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(_msAdpcmMonoFixturePath, destWavPath, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(22050);
                wavReader.TotalSamples.Should().Be(22050);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var expected = ReadGroundTruthPcm16(_msAdpcmMonoExpectedPcmPath);
                buffer.Should().Equal(expected[0..22050]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static int[] ReadGroundTruthPcm16(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new int[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8));
            }

            return samples;
        }

        private static byte[] ReadDataChunkBytes(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var dataIndex = 0;
            for (var i = 12; i < bytes.Length - 8; i++)
            {
                if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
                {
                    dataIndex = i;
                    break;
                }
            }

            var dataSize = BitConverter.ToUInt32(bytes, dataIndex + 4);

            return bytes[(dataIndex + 8)..(int)(dataIndex + 8 + dataSize)];
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Convert_G711WavToWav_Should_Reproduce_BitExact_Samples(WavSampleFormat sampleFormat)
        {
            var (sourcePath, expectedPath) = sampleFormat == WavSampleFormat.MuLaw
                ? (_muLawMonoFixturePath, _muLawMonoExpectedPcmPath)
                : (_aLawMonoFixturePath, _aLawMonoExpectedPcmPath);

            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destWavPath);

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(8000);
                wavReader.TotalSamples.Should().Be(16000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                buffer.Should().Equal(ReadGroundTruthPcm16(expectedPath));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Convert_WavToG711Wav_Should_Produce_BitExact_Bytes_Against_FfmpegEncodedFixture(WavSampleFormat sampleFormat)
        {
            var (fixturePath, expectedPcmPath) = sampleFormat == WavSampleFormat.MuLaw
                ? (_muLawMonoFixturePath, _muLawMonoExpectedPcmPath)
                : (_aLawMonoFixturePath, _aLawMonoExpectedPcmPath);

            var tempDirectory = CreateTempDirectory();

            try
            {
                // Unlike IMA ADPCM (decode-only), G.711 supports the encode direction too -- this
                // proves AudioCutter.Convert's generic WavSampleFormat destination routing (already
                // proven for Float32) now also reaches WavWriter's new G.711 encode path, producing
                // bytes that match a real ffmpeg encoder exactly, not just this project's own decoder.
                var groundTruthSamples = ReadGroundTruthPcm16(expectedPcmPath);
                var sourceWavPath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourceWavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, groundTruthSamples);

                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourceWavPath, destPath, sampleFormat);

                ReadDataChunkBytes(destPath).Should().Equal(ReadDataChunkBytes(fixturePath));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Cut_G711Wav_Should_Extract_Exact_Sample_Range(WavSampleFormat sampleFormat)
        {
            var (sourcePath, expectedPath) = sampleFormat == WavSampleFormat.MuLaw
                ? (_muLawMonoFixturePath, _muLawMonoExpectedPcmPath)
                : (_aLawMonoFixturePath, _aLawMonoExpectedPcmPath);

            var tempDirectory = CreateTempDirectory();

            try
            {
                var destWavPath = Path.Combine(tempDirectory, "cut.wav");

                AudioCutter.Cut(sourcePath, destWavPath, startInSeconds: 0, endInSeconds: 1).Should().BeTrue();

                using var wavReader = WavReader.Open(destWavPath);
                wavReader.Channels.Should().Be(1);
                wavReader.SampleRate.Should().Be(8000);
                wavReader.TotalSamples.Should().Be(8000);

                var buffer = new int[wavReader.TotalSamples * wavReader.Channels];
                wavReader.ReadInterleavedSamples(buffer, (int)wavReader.TotalSamples);

                var expected = ReadGroundTruthPcm16(expectedPath);
                buffer.Should().Equal(expected[0..8000]);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static (string FilePath, int[] InterleavedSamples) CreateRampTta(string tempDirectory, string fileName, int totalFrames, int sampleRate)
        {
            var filePath = Path.Combine(tempDirectory, fileName);
            var interleavedSamples = new int[totalFrames];

            for (var frame = 0; frame < totalFrames; frame++)
            {
                interleavedSamples[frame] = frame % 1000;
            }

            using (var session = TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate, bitsPerSample: 16))
            {
                session.WriteInterleavedSamples(interleavedSamples, totalFrames);
                session.Finish();
            }

            return (filePath, interleavedSamples);
        }
    }
}
