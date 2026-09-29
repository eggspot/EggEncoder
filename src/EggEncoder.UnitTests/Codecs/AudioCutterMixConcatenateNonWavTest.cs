using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    // Mix/Concatenate coverage against a non-WAV source codec. WMA (pure managed WMAv2, see
    // EggEncoder.Codecs.Wma) is used because -- unlike MP3/FLAC -- it needs no native win-x64 DLL, so
    // this runs on any dev machine and in CI on every OS, not just Windows. WMA is lossy, so assertions
    // here compare relative signal energy (RMS) and approximate frame counts rather than exact sample
    // values -- the same style AudioCutterTest and WmaEncoderTest already use for WMA round-trips.
    public class AudioCutterMixConcatenateNonWavTest
    {
        private const int SampleRate = 8000; // <= 16000 Hz uses WmaTables' 512-frame block length

        [Fact]
        public void Concatenate_TwoWmaSources_Should_Preserve_Order_And_Approximate_Total_Length()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var silencePath = Path.Combine(tempDirectory, "silence.wma");
                var tonePath = Path.Combine(tempDirectory, "tone.wma");
                CreateWmaFile(silencePath, channels: 1, SampleRate, new int[2048]);
                CreateWmaFile(tonePath, channels: 1, SampleRate, GenerateTone(2048, amplitude: 12000));
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                AudioCutter.Concatenate([silencePath, tonePath], destPath);

                using var reader = WavReader.Open(destPath);
                reader.Channels.Should().Be(1);
                reader.SampleRate.Should().Be(SampleRate);
                reader.TotalSamples.Should().BeInRange(4096, 4096 + 1024); // 2 sources x 2048 frames, plus at most one frame of WMA block padding per source

                var buffer = new int[reader.TotalSamples];
                reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

                var firstHalf = buffer.Take(buffer.Length / 2).ToArray();
                var secondHalf = buffer.Skip(buffer.Length / 2).ToArray();
                Rms(firstHalf).Should().BeLessThan(Rms(secondHalf) / 5); // silence, then tone -- order preserved
                Rms(secondHalf).Should().BeGreaterThan(1000); // the tone actually decoded to a real signal
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Concatenate_MismatchedChannelCount_WmaSources_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var monoPath = Path.Combine(tempDirectory, "mono.wma");
                var stereoPath = Path.Combine(tempDirectory, "stereo.wma");
                CreateWmaFile(monoPath, channels: 1, SampleRate, GenerateTone(2048, amplitude: 8000));
                CreateWmaFile(stereoPath, channels: 2, SampleRate, InterleaveStereo(GenerateTone(2048, amplitude: 8000)));
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                var act = () => AudioCutter.Concatenate([monoPath, stereoPath], destPath);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_TwoWmaSources_Should_Combine_Louder_Than_Either_Alone()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wma");
                var secondPath = Path.Combine(tempDirectory, "second.wma");
                CreateWmaFile(firstPath, channels: 1, SampleRate, GenerateTone(2048, amplitude: 8000));
                CreateWmaFile(secondPath, channels: 1, SampleRate, GenerateTone(2048, amplitude: 8000, frequencyHz: 660));
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                AudioCutter.Mix([new MixInput(firstPath), new MixInput(secondPath)], destPath);

                using var reader = WavReader.Open(destPath);
                var buffer = new int[reader.TotalSamples];
                reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);
                var mixedRms = Rms(buffer);

                // Baseline against the *actual* round-tripped solo signals (not an idealized generated
                // tone), since WMA's own lossy encoding already changes each source's energy somewhat --
                // comparing against an un-round-tripped ideal would conflate codec lossiness with Mix's
                // own behavior.
                var soloFirstRms = Rms(DecodeWma(firstPath));
                var soloSecondRms = Rms(DecodeWma(secondPath));

                mixedRms.Should().BeGreaterThan(Math.Max(soloFirstRms, soloSecondRms));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_DifferentLengthWmaSources_Should_Pad_Shorter_With_Silence()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var longPath = Path.Combine(tempDirectory, "long.wma");
                var shortPath = Path.Combine(tempDirectory, "short.wma");
                CreateWmaFile(longPath, channels: 1, SampleRate, GenerateTone(4096, amplitude: 10000));
                CreateWmaFile(shortPath, channels: 1, SampleRate, GenerateTone(2048, amplitude: 10000, frequencyHz: 660));
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                AudioCutter.Mix([new MixInput(longPath), new MixInput(shortPath)], destPath);

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().BeInRange(4096, 4096 + 1024); // padded to the longer source, not truncated to the shorter

                var buffer = new int[reader.TotalSamples];
                reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

                var overlapping = buffer.Take(2000).ToArray(); // both sources contributing
                var tailOnly = buffer.Skip(buffer.Length - 2000).ToArray(); // only the longer source, short one silence-padded
                Rms(overlapping).Should().BeGreaterThan(Rms(tailOnly));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_MismatchedSampleRate_WmaSources_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wma");
                var secondPath = Path.Combine(tempDirectory, "second.wma");
                CreateWmaFile(firstPath, channels: 1, sampleRate: 8000, GenerateTone(2048, amplitude: 8000));
                CreateWmaFile(secondPath, channels: 1, sampleRate: 11025, GenerateTone(2048, amplitude: 8000));
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                var act = () => AudioCutter.Mix([new MixInput(firstPath), new MixInput(secondPath)], destPath);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static void CreateWmaFile(string filePath, int channels, int sampleRate, int[] interleavedSamples)
        {
            using var session = WmaEncoderSession.OpenSession(filePath, channels, sampleRate);
            session.WriteInterleavedSamples(interleavedSamples, interleavedSamples.Length / channels);
            session.Finish();
        }

        private static int[] DecodeWma(string filePath)
        {
            var samples = new List<int>();
            WmaDecoder.Decode(filePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));
            return [.. samples];
        }

        private static int[] GenerateTone(int frameCount, int amplitude, double frequencyHz = 440)
        {
            var samples = new int[frameCount];
            for (var i = 0; i < frameCount; i++)
            {
                samples[i] = (int)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
            }

            return samples;
        }

        private static int[] InterleaveStereo(int[] mono)
        {
            var stereo = new int[mono.Length * 2];
            for (var i = 0; i < mono.Length; i++)
            {
                stereo[i * 2] = mono[i];
                stereo[(i * 2) + 1] = mono[i];
            }

            return stereo;
        }

        private static double Rms(int[] samples) => samples.Length == 0 ? 0.0 : Math.Sqrt(samples.Average(s => (double)s * s));

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            return tempDirectory;
        }
    }
}
