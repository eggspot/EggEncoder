using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Au;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.Wma;
using EggEncoder.Pcm;
using EggEncoder.UnitTests.TestUtilities;
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

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Opus);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavSourceToFlacDest_Should_Decode_And_Encode_Correctly()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 10000, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Flac);

                destStream.Position = 0;
                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(destStream);
                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(8000);
                streamInfo.BitsPerSample.Should().Be(16);
                decodedSamples.Should().Equal(samples, "FLAC is lossless -- the decoded samples must match exactly, not just approximately");
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithNonSeekableFlacDest_Should_Throw()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new NonSeekableWriteStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Flac);

                act.Should().ThrowExactly<ArgumentException>("FlacEncoderSession.Finish() seeks back to patch STREAMINFO, which a non-seekable destination (e.g. an HTTP response body) cannot support");
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithResamplingPipeline_FlacDest_Should_WriteCorrectTotalFrames()
        {
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 8000, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 8000, targetRate: 16000, channels: 2));
                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Flac, pipeline);

                destStream.Position = 0;
                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(destStream);
                streamInfo.SampleRate.Should().Be(16000);
                (decodedSamples.Length / streamInfo.Channels).Should().Be(16000);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavSourceToTtaDest_Should_Decode_And_Encode_Correctly()
        {
            var (sourcePath, samples) = CreateRampWavFile(totalFrames: 10000, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Tta);

                destStream.Position = 0;
                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destStream, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));
                streamInfo.Channels.Should().Be(2);
                streamInfo.SampleRate.Should().Be(8000);
                streamInfo.BitsPerSample.Should().Be(16);
                decoded.Should().Equal(samples, "TTA is lossless -- the decoded samples must match exactly, not just approximately");
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithNonSeekableTtaDest_Should_StillWork()
        {
            // Unlike Flac, TtaEncoderSession.OpenSession(Stream, ...) needs no CanSeek at all --
            // confirmed through the full AudioCutter.Convert call, not just the writer in isolation.
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 8000);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new NonSeekableWriteStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Tta);

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavSourceToAacDest_Should_Decode_And_Encode_Correctly()
        {
            const int sampleRate = 44100;
            const int sampleCount = sampleRate;

            // A sine tone, not CreateRampWavFile's own linear ramp -- AAC is a lossy, psychoacoustic
            // codec, and a ramp has neither periodic structure nor bounded int16-range amplitude
            // (CreateRampWavFile's own ramp runs up to totalFrames, far past short.MaxValue here).
            var originalSamples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                originalSamples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var sourcePath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_aac_src_{Guid.NewGuid():N}.wav");
            try
            {
                using (var writer = WavWriter.Create(sourcePath, channels: 1, sampleRate, bitsPerSample: 16, sampleCount))
                {
                    writer.WriteInterleavedSamples(originalSamples, sampleCount);
                }

                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Aac);

                destStream.Position = 0;
                var decodedSamples = new List<int>();
                var streamInfo = AacDecoder.Decode(destStream, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);

                // AacEncoderTest.Encode_ThenDecode_Should_ReconstructToneSignal_UpToScale documents
                // the same one-frame (1024-sample) encoder delay for this exact codec path.
                const int encoderDelaySamples = 1024;
                var compareLength = Math.Min(decodedSamples.Count - encoderDelaySamples, originalSamples.Length);
                compareLength.Should().BeGreaterThan(1000);

                double dotProduct = 0;
                double originalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dotProduct += (double)decodedSamples[i + encoderDelaySamples] * originalSamples[i];
                    originalEnergy += (double)originalSamples[i] * originalSamples[i];
                }

                originalEnergy.Should().BeGreaterThan(0);
                var scale = dotProduct / originalEnergy;

                double errorEnergy = 0;
                double signalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    var expected = originalSamples[i] * scale;
                    var error = decodedSamples[i + encoderDelaySamples] - expected;
                    errorEnergy += error * error;
                    signalEnergy += expected * expected;
                }

                var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));
                signalToNoiseRatioDb.Should().BeGreaterThan(15, $"expected SNR > 15dB for a lossy AAC round trip (scale={scale}), got {signalToNoiseRatioDb}dB");
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavStereoSourceToAacDest_Should_Throw()
        {
            // AacEncoderSession only supports mono -- confirmed through the full AudioCutter.Convert
            // call, not just OpenSession in isolation, so the channel count genuinely propagates
            // from the decoded source through to the destination sink's own validation.
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 44100);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Aac);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithNonSeekableAacDest_Should_StillWork()
        {
            // Unlike Flac, AacEncoderSession.OpenSession(Stream, ...) needs no CanSeek at all --
            // confirmed through the full AudioCutter.Convert call, not just the writer in isolation.
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 44100, channels: 1);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new NonSeekableWriteStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Aac);

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WavSourceToWmaDest_Should_Decode_And_Encode_Correctly()
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int sampleCount = sampleRate;

            // A sine tone per channel, not CreateRampWavFile's own linear ramp -- WMA, like AAC, is
            // a lossy codec, so the comparison needs real periodic structure to measure SNR against.
            var originalSamples = new int[sampleCount * channels];
            for (var i = 0; i < sampleCount; i++)
            {
                originalSamples[(i * channels) + 0] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                originalSamples[(i * channels) + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
            }

            var sourcePath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_wma_src_{Guid.NewGuid():N}.wav");
            try
            {
                using (var writer = WavWriter.Create(sourcePath, channels, sampleRate, bitsPerSample: 16, sampleCount))
                {
                    writer.WriteInterleavedSamples(originalSamples, sampleCount);
                }

                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new MemoryStream();

                AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wma);

                destStream.Position = 0;
                var decodedSamples = new List<int>();
                var streamInfo = WmaDecoder.Decode(destStream, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(sampleRate);
                decodedSamples.Should().NotBeEmpty();

                // WmaEncoderTest.AssertRoundTripSnr documents the same one-frame encoder delay for
                // this exact codec path.
                var encoderDelaySamples = 1 << WmaTables.GetFrameLengthBits(sampleRate);

                for (var channel = 0; channel < channels; channel++)
                {
                    var originalChannel = new List<double>();
                    for (var i = channel; i < originalSamples.Length; i += channels)
                    {
                        originalChannel.Add(originalSamples[i]);
                    }

                    var decodedChannel = new List<double>();
                    for (var i = channel; i < decodedSamples.Count; i += channels)
                    {
                        decodedChannel.Add(decodedSamples[i]);
                    }

                    var compareLength = Math.Min(decodedChannel.Count - encoderDelaySamples, originalChannel.Count);
                    compareLength.Should().BeGreaterThan(1000);

                    double dotProduct = 0;
                    double originalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        dotProduct += decodedChannel[i + encoderDelaySamples] * originalChannel[i];
                        originalEnergy += originalChannel[i] * originalChannel[i];
                    }

                    originalEnergy.Should().BeGreaterThan(0);
                    var scale = dotProduct / originalEnergy;

                    double errorEnergy = 0;
                    double signalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        var expected = originalChannel[i] * scale;
                        var error = decodedChannel[i + encoderDelaySamples] - expected;
                        errorEnergy += error * error;
                        signalEnergy += expected * expected;
                    }

                    var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));
                    signalToNoiseRatioDb.Should().BeGreaterThan(10, $"expected SNR > 10dB for a lossy WMA round trip, channel {channel} (scale={scale}), got {signalToNoiseRatioDb}dB");
                }
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        [Fact]
        public void Convert_Stream_WithNonSeekableWmaDest_Should_StillWork()
        {
            // Unlike Flac, WmaEncoderSession.OpenSession(Stream, ...) needs no CanSeek at all --
            // confirmed through the full AudioCutter.Convert call, not just the writer in isolation.
            var (sourcePath, _) = CreateRampWavFile(totalFrames: 10, sampleRate: 44100);
            try
            {
                using var sourceStream = File.OpenRead(sourcePath);
                using var destStream = new NonSeekableWriteStream();

                var act = () => AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, AudioFormat.Wma);

                act.Should().NotThrow();
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

        [Theory]
        [InlineData(AudioFormat.Aac)]
        [InlineData(AudioFormat.Wma)]
        public void Convert_Stream_TwentyFourBitWav_ToSixteenBitOnlyLossyCodec_Should_Match_Equivalent_SixteenBitSource(AudioFormat destFormat)
        {
            // Regression: OpenSinkStream used to drop bitsPerSample for AAC/WMA, so a 24-bit source
            // reached a 16-bit-only codec core unscaled and encoded as wrapped/clipped noise.
            // Compared after decode: WMA's ASF header carries a fresh random file ID every time.
            const int sampleRate = 44100;
            const int totalFrames = 5000;
            var sixteenBitSamples = new int[totalFrames];
            for (var i = 0; i < totalFrames; i++)
            {
                sixteenBitSamples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var sixteenBitOutput = ConvertMonoWavStream(sixteenBitSamples, bitsPerSample: 16, sampleRate, destFormat);
            var twentyFourBitOutput = ConvertMonoWavStream(sixteenBitSamples.Select(sample => sample << 8).ToArray(), bitsPerSample: 24, sampleRate, destFormat);

            twentyFourBitOutput.Should().Equal(sixteenBitOutput);
        }

        private static List<int> ConvertMonoWavStream(int[] samples, int bitsPerSample, int sampleRate, AudioFormat destFormat)
        {
            using var sourceStream = new MemoryStream();
            using (var writer = WavWriter.Create(sourceStream, channels: 1, sampleRate, bitsPerSample, samples.Length))
            {
                writer.WriteInterleavedSamples(samples, samples.Length);
                writer.Finish();
            }

            sourceStream.Position = 0;
            using var destStream = new MemoryStream();
            AudioCutter.Convert(sourceStream, AudioFormat.Wav, destStream, destFormat);

            destStream.Position = 0;
            var decoded = new List<int>();
            AudioBlockDecodedCallback onBlock = (block, _, _, _, _) => decoded.AddRange(block.ToArray());
            if (destFormat == AudioFormat.Aac)
            {
                AacDecoder.Decode(destStream, onBlock);
            }
            else
            {
                WmaDecoder.Decode(destStream, onBlock);
            }

            return decoded;
        }

        private static (string FilePath, int[] Samples) CreateRampWavFile(int totalFrames, int sampleRate, int channels = 2)
        {
            var samples = BuildRampSamples(totalFrames, channels);
            var filePath = Path.Combine(Path.GetTempPath(), $"audiocutter_stream_{Guid.NewGuid():N}.wav");

            using (var writer = WavWriter.Create(filePath, channels, sampleRate, bitsPerSample: 16, totalFrames))
            {
                writer.WriteInterleavedSamples(samples, totalFrames);
            }

            return (filePath, samples);
        }
    }
}
