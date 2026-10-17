using EggEncoder.Codecs;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Au;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Wav;
using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    public class AudioCutterStreamTest
    {
        [Fact]
        public void Convert_Stream_WavToWav_Should_RoundTrip_SameAs_PathBased()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 500, sampleRate: 8000);
            try
            {
                var pathDestPath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_path_{Guid.NewGuid():N}.wav");
                try
                {
                    AudioCutter.Convert(sourcePath, pathDestPath);
                    var pathBasedBytes = File.ReadAllBytes(pathDestPath);

                    using var sourceStream = File.OpenRead(sourcePath);
                    using var destStream = new MemoryStream();

                    AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav);

                    destStream.ToArray().Should().Equal(pathBasedBytes);
                }
                finally
                {
                    File.Delete(pathDestPath);
                }
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_FlacSourceToWavDest_Should_Decode_And_Encode_Correctly()
        {
            var samples = BuildRampSamples(totalFrames: 1000, channels: 2);
            var flacPath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_src_{Guid.NewGuid():N}.flac");
            try
            {
                using (var session = FlacEncoder.OpenSession(flacPath, channels: 2, bitsPerSample: 16, sampleRate: 8000))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / 2);
                    session.Finish();
                }

                using var sourceStream = File.OpenRead(flacPath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Flac, destStream, AudioFormat.Wav);

                destStream.Position = 0;
                using var reader = WavReader.Open(destStream);
                reader.Channels.Should().Be(2);
                reader.SampleRate.Should().Be(8000);
                reader.BitsPerSample.Should().Be(16);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(flacPath);
            }
        }

        [Fact]
        public void Convert_Stream_WithVolumePipeline_Should_ApplyTransform()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 200, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var pipeline = new PcmTransformPipeline(new VolumeTransform(2.0));
                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav, pipeline);

                destStream.Position = 0;
                using var reader = WavReader.Open(destStream);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);

                // Every ramp sample is doubled by VolumeTransform(2.0), except where that would overflow
                // the 16-bit native range -- none of this fixture's own small ramp values do.
                var expected = samples.Select(s => s * 2).ToArray();
                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithResamplingPipeline_Should_Use_DeferredSink_And_WriteCorrectTotalFrames()
        {
            // ResamplingTransform.CanChangeFrameCount is true, so the destination's own exact frame
            // count isn't known until every block has been decoded -- proving OpenSinkStream's own
            // DeferredFixedHeaderSink path (not just the "exact count known up front" one) works
            // correctly for a Stream destination too.
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 8000, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 8000, targetRate: 16000, channels: 2));
                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav, pipeline);

                destStream.Position = 0;
                using var reader = WavReader.Open(destStream);
                reader.SampleRate.Should().Be(16000);
                reader.TotalSamples.Should().Be(16000); // 1.0s of 8000Hz source resampled to 16000Hz
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithUnsupportedSourceFormat_Should_Throw()
        {
            using var sourceStream = new MemoryStream();
            using var destStream = new MemoryStream();

            var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Mp3, destStream, AudioFormat.Wav);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void Convert_Stream_WavSourceToAiffDest_Should_Decode_And_Encode_Correctly()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 300, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Aiff);

                destStream.Position = 0;
                using var reader = AiffReader.Open(destStream);
                reader.Channels.Should().Be(2);
                reader.SampleRate.Should().Be(8000);
                reader.BitsPerSample.Should().Be(16);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavSourceToAuDest_Should_Decode_And_Encode_Correctly()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 300, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Au);

                destStream.Position = 0;
                using var reader = AuReader.Open(destStream);
                reader.Channels.Should().Be(2);
                reader.SampleRate.Should().Be(8000);
                reader.BitsPerSample.Should().Be(16);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithResamplingPipeline_AiffDest_Should_Use_DeferredSink_And_WriteCorrectTotalFrames()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 8000, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 8000, targetRate: 16000, channels: 2));
                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Aiff, pipeline);

                destStream.Position = 0;
                using var reader = AiffReader.Open(destStream);
                reader.SampleRate.Should().Be(16000);
                reader.TotalSamples.Should().Be(16000);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithUnsupportedDestFormat_Should_Throw()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Flac);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_Should_NotDispose_SourceOrDestStream()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 8000);
            try
            {
                var sourceStream = File.OpenRead(sourcePath);
                var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav);

                var sourceAct = () => sourceStream.Position;
                var destAct = () => destStream.Position;
                sourceAct.Should().NotThrow();
                destAct.Should().NotThrow();

                sourceStream.Dispose();
                destStream.Dispose();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithNullSource_Should_Throw()
        {
            using var destStream = new MemoryStream();
            var act = () => AudioCutter.Convert((Stream)null!, AudioFormat.Wav, destStream, AudioFormat.Wav);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Convert_Stream_WithNullDest_Should_Throw()
        {
            using var sourceStream = new MemoryStream();
            var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, (Stream)null!, AudioFormat.Wav);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Convert_Stream_WithAlreadyCancelledToken_Should_Throw_And_WriteNothing()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wav, cts.Token);

                act.Should().Throw<OperationCanceledException>();
                destStream.Length.Should().Be(0);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Cut_Stream_WavToWav_Should_TrimCorrectly()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 1000, sampleRate: 1000);
            try
            {
                var pathDestPath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_cut_path_{Guid.NewGuid():N}.wav");
                try
                {
                    AudioCutter.Cut(sourcePath, pathDestPath, startInSeconds: 0, endInSeconds: 1);
                    var pathBasedBytes = File.ReadAllBytes(pathDestPath);

                    using var sourceStream = File.OpenRead(sourcePath);
                    using var destStream = new MemoryStream();

                    var produced = AudioCutter.Cut(sourceStream, AudioFormat.Wav, startInSeconds: 0, endInSeconds: 1, destStream, AudioFormat.Wav);

                    produced.Should().BeTrue();
                    destStream.ToArray().Should().Equal(pathBasedBytes);
                }
                finally
                {
                    File.Delete(pathDestPath);
                }
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Cut_Stream_WithRangeOutsideDuration_Should_ReturnFalse_And_WriteNothing()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 1000, sampleRate: 1000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var produced = AudioCutter.Cut(sourceStream, AudioFormat.Wav, startInSeconds: 10, endInSeconds: 20, destStream, AudioFormat.Wav);

                produced.Should().BeFalse();
                destStream.Length.Should().Be(0);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Cut_Stream_WithNullOptions_Should_Throw()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 1000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var act = () => AudioCutter.Cut(sourceStream, AudioFormat.Wav, 0, 1, destStream, AudioFormat.Wav, options: null!);

                act.Should().Throw<ArgumentNullException>();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        private static int[] BuildRampSamples(int totalFrames, int channels)
        {
            var samples = new int[totalFrames * channels];
            for (var frame = 0; frame < totalFrames; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = channel == 0 ? frame : -frame;
                }
            }

            return samples;
        }

        private static (string FilePath, int[] Samples) CreateRampWavFile(int totalFrames, int sampleRate)
        {
            var samples = BuildRampSamples(totalFrames, channels: 2);
            var filePath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_{Guid.NewGuid():N}.wav");

            using (var writer = WavWriter.Create(filePath, channels: 2, sampleRate, bitsPerSample: 16, totalFrames))
            {
                writer.WriteInterleavedSamples(samples, totalFrames);
            }

            return (filePath, samples);
        }
    }
}
