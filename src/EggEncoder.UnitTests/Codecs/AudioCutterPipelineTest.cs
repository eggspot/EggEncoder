using EggEncoder.Codecs;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Pcm;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    // WAV/AIFF/CAF-only: MP3/FLAC round-trips depend on native win-x64 DLLs that can't load on this
    // (non-Windows) dev machine -- see AudioCutterTest for that coverage, which runs in CI on Windows.
    // These tests exercise the pipeline plumbing itself (format negotiation, DeferredFixedHeaderSink,
    // Mix, Concatenate), which is codec-agnostic.
    public class AudioCutterPipelineTest
    {
        [Fact]
        public void Convert_WithVolumeTransform_Should_Scale_Every_Sample()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, samples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                using var reader = WavReader.Open(destPath);
                reader.Channels.Should().Be(2);
                reader.SampleRate.Should().Be(1000);
                reader.TotalSamples.Should().Be(100);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, 100);
                buffer.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_SpanningMultipleDecodeBlocks_Should_Write_Every_Frame()
        {
            // 5000 frames spans two 4096-frame decode blocks. Volume never changes frame count, so this
            // exercises the direct-WavWriter path (opened once with the exact known total up front) across
            // more than one WriteInterleavedSamples call, rather than DeferredFixedHeaderSink's buffer-then-flush.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, samples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5000, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(5000);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, 5000);
                buffer.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void MeasurePeakAmplitude_Then_PeakNormalization_Should_Normalize_By_The_True_WholeFile_Peak()
        {
            // The documented correct usage for peak normalization through a block-by-block pipeline:
            // measure the true whole-file peak first, then hand it to the transform before the pipeline
            // ever sees a single decode block.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [100, -32768, 200]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var (peak, peakBitsPerSample) = AudioCutter.MeasurePeakAmplitude(sourcePath);
                peak.Should().Be(32768);
                peakBitsPerSample.Should().Be(16);

                var normalize = new PeakNormalizationTransform(targetDb: 0.0);
                normalize.MeasurePeak(peak, peakBitsPerSample);

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(normalize));

                using var reader = WavReader.Open(destPath);
                reader.BitsPerSample.Should().Be(16);
                var buffer = new int[3];
                reader.ReadInterleavedSamples(buffer, 3);

                // Gain is computed against the 16-bit native range (-32768..32767), not a fixed 32-bit
                // scale -- otherwise this would blow the samples miles outside what a 16-bit WAV can hold.
                var expectedGain = 1.0 * 32767 / peak;
                buffer[1].Should().Be((int)Math.Clamp(-32768 * expectedGain, -32768, 32767));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithResamplingTransform_Should_Produce_Exact_Frame_Count_For_A_Single_Block()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                // 100 frames comfortably fits in one 4096-frame decode block, so the output frame count
                // is exactly round(100 * 2) with no cross-block rounding to account for.
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 2));
                AudioCutter.Convert(sourcePath, destPath, pipeline);

                using var reader = WavReader.Open(destPath);
                reader.SampleRate.Should().Be(2000);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(200);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_AiffDestination_Should_Scale_Every_Sample()
        {
            // AIFF has the same exact-frame-count-up-front constraint as WAV (see AiffWriter.Create);
            // volume never changes frame count, so this exercises AIFF's direct-writer path (the total
            // is known before the first sample is written) rather than DeferredFixedHeaderSink.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, samples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.aiff");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                using var reader = AiffReader.Open(destPath);
                reader.Channels.Should().Be(2);
                reader.SampleRate.Should().Be(1000);
                reader.TotalSamples.Should().Be(100);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, 100);
                buffer.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithResamplingTransform_AiffDestination_Should_Produce_Exact_Frame_Count()
        {
            // Resampling changes the frame count unpredictably from the pipeline's perspective, so the
            // AIFF destination must go through DeferredFixedHeaderSink (buffer everything, then open the
            // real AiffWriter once the true total is known) exactly like the WAV destination case above.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.aiff");

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 2));
                AudioCutter.Convert(sourcePath, destPath, pipeline);

                using var reader = AiffReader.Open(destPath);
                reader.SampleRate.Should().Be(2000);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(200);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithResamplingTransform_WavPackDestination_Should_Produce_Exact_Frame_Count()
        {
            // Same rationale as the AIFF case above: resampling changes the frame count
            // unpredictably from the pipeline's perspective, so the WavPack destination must go
            // through DeferredFixedHeaderSink too, since WavpackSetConfiguration64 needs an exact
            // total sample count up front just like WavWriter/AiffWriter do.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 100, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wv");

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 2));
                AudioCutter.Convert(sourcePath, destPath, pipeline);

                var streamInfo = WavPackDecoder.Decode(destPath, (_, _, _, _, _) => { });

                streamInfo.SampleRate.Should().Be(2000);
                streamInfo.Channels.Should().Be(2);
                streamInfo.TotalSamples.Should().Be(200);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_AiffSourceAndDestination_WithResamplingTransform_Should_Produce_Exact_Frame_Count()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.aiff");
                var samples = Enumerable.Range(0, 200).SelectMany(frame => new[] { frame, -frame }).ToArray();
                AiffFileBuilder.Create(sourcePath, channels: 2, sampleRate: 1000, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.aiff");

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 500, channels: 2));
                AudioCutter.Convert(sourcePath, destPath, pipeline);

                using var reader = AiffReader.Open(destPath);
                reader.SampleRate.Should().Be(500);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(100);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_CafDestination_Should_Scale_Every_Sample()
        {
            // Unlike WAV/AIFF, AlacEncoderSession needs no exact frame count up front (ALAC packets
            // carry their own sample count), so a CAF destination always streams straight through
            // OpenSinkForPipeline's fallback to OpenSink -- this just confirms that dispatch actually
            // reaches AlacEncoderSession and the pipeline's transform is applied before encoding. ALAC
            // in this codebase is mono-only (see AlacDecoder's doc comment), unlike the stereo ramp
            // fixture most of the other tests in this file share.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var samples = Enumerable.Range(0, 100).ToArray();
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.caf");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(100);
                decoded.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_TtaDestination_Should_Scale_Every_Sample()
        {
            // Like CAF, a TTA destination streams straight through OpenSinkForPipeline's fallback to
            // OpenSink -- TtaEncoderSession also needs no exact frame count up front. This confirms
            // the dispatch reaches TtaEncoderSession and the pipeline's transform is applied before
            // encoding.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var samples = Enumerable.Range(0, 100).ToArray();
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.tta");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(100);
                decoded.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_WavPackDestination_Should_Scale_Every_Sample()
        {
            // Unlike TTA/CAF above, a WavPack destination is NOT a generic-fallback case --
            // OpenSinkForPipeline special-cases ".wv" the same way it does ".wav"/".aiff", since
            // WavpackSetConfiguration64 needs an exact total sample count up front. With no
            // frame-count-changing transform in this pipeline, exactTotalFrames is known, so this
            // exercises that special-case's "open WavPackEncoderSession directly" branch (not its
            // DeferredFixedHeaderSink fallback) and confirms the transform is applied before encoding.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var samples = Enumerable.Range(0, 100).ToArray();
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 1000, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.wv");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                var decoded = new List<int>();
                var streamInfo = WavPackDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(1000);
                streamInfo.TotalSamples.Should().Be(100);
                decoded.Should().Equal(samples.Select(s => s * 2));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_OpusDestination_Should_Scale_The_Signal()
        {
            // Opus is lossy and fixed at 48kHz (see OpusEncoderSession's own doc comment), so this
            // can't check exact sample equality the way the CAF/TTA versions above do -- instead it
            // fits a best-fit scale factor between the decoded signal and the *original, unscaled*
            // tone (the same technique OpusEncoderSessionTest uses for its own SNR checks) and
            // confirms that scale comes out close to the VolumeTransform's own 2.0, proving the
            // transform was genuinely applied before encoding rather than skipped.
            const int sampleRate = 48000;
            var tempDirectory = CreateTempDirectory();
            try
            {
                var samples = new int[sampleRate];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(4000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.opus");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                var decoded = new List<int>();
                var streamInfo = OpusDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);

                var compareLength = Math.Min(decoded.Count, samples.Length);
                double dotProduct = 0;
                double originalEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dotProduct += decoded[i] * (double)samples[i];
                    originalEnergy += (double)samples[i] * samples[i];
                }

                var scale = dotProduct / originalEnergy;
                scale.Should().BeApproximately(2.0, 0.2, $"VolumeTransform(2.0) should have doubled the signal before encoding, got fitted scale {scale}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithVolumeTransform_VorbisDestination_Should_Scale_The_Signal()
        {
            // Mirrors Convert_WithVolumeTransform_OpusDestination_Should_Scale_The_Signal's technique,
            // but Vorbis has no pre_skip-equivalent field (see VorbisEncoderSessionTest's remarks), so
            // decoded[0] does not line up with original[0] -- this finds the true alignment via
            // normalized cross-correlation first, then fits the scale at that offset.
            const int sampleRate = 44100;
            var tempDirectory = CreateTempDirectory();
            try
            {
                var samples = new int[sampleRate];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(4000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                }

                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.ogg");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(destPath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);
                decoded.Should().NotBeEmpty();

                var compareLength = Math.Min(2000, samples.Length);
                var bestOffset = 0;
                var bestCorrelation = double.MinValue;
                for (var offset = 0; offset <= sampleRate / 4 && offset + compareLength <= decoded.Count; offset++)
                {
                    double dot = 0, originalEnergy = 0, decodedEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        dot += decoded[offset + i] * (double)samples[i];
                        originalEnergy += (double)samples[i] * samples[i];
                        decodedEnergy += (double)decoded[offset + i] * decoded[offset + i];
                    }

                    var normalized = originalEnergy > 0 && decodedEnergy > 0 ? dot / Math.Sqrt(originalEnergy * decodedEnergy) : 0;
                    if (normalized > bestCorrelation)
                    {
                        bestCorrelation = normalized;
                        bestOffset = offset;
                    }
                }

                var fitLength = Math.Min(decoded.Count - bestOffset, samples.Length);
                double fitDotProduct = 0;
                double fitOriginalEnergy = 0;
                for (var i = 0; i < fitLength; i++)
                {
                    fitDotProduct += decoded[bestOffset + i] * (double)samples[i];
                    fitOriginalEnergy += (double)samples[i] * samples[i];
                }

                var scale = fitDotProduct / fitOriginalEnergy;
                scale.Should().BeApproximately(2.0, 0.2, $"VolumeTransform(2.0) should have doubled the signal before encoding, got fitted scale {scale} at offset {bestOffset}");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void WriteWavFromFloat_Then_ReadWavAsFloat_Should_RoundTrip()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var destPath = Path.Combine(tempDirectory, "float.wav");
                float[] original = [0.5f, -0.5f, 1.0f, -1.0f, 0.0f, 0.25f];

                AudioCutter.WriteWavFromFloat(destPath, original, channels: 2, sampleRate: 44100);

                using (var reader = WavReader.Open(destPath))
                {
                    reader.IsFloatFormat.Should().BeTrue();
                    reader.BitsPerSample.Should().Be(32);
                    reader.Channels.Should().Be(2);
                    reader.SampleRate.Should().Be(44100);
                }

                var (samples, channels, sampleRate) = AudioCutter.ReadWavAsFloat(destPath);

                channels.Should().Be(2);
                sampleRate.Should().Be(44100);
                samples.Should().HaveCount(original.Length);
                for (var i = 0; i < original.Length; i++)
                {
                    samples[i].Should().BeApproximately(original[i], 0.0001f);
                }
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void WriteWavFromFloat_NonPositiveChannels_Should_Throw()
        {
            var act = () => AudioCutter.WriteWavFromFloat("dest.wav", [0.5f, -0.5f], channels: 0, sampleRate: 44100);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void WriteWavFromFloat_SampleCountNotAWholeNumberOfFrames_Should_Throw()
        {
            // 3 samples doesn't divide evenly into 2-channel frames -- silently truncating via integer
            // division would drop the trailing sample instead of erroring.
            var act = () => AudioCutter.WriteWavFromFloat("dest.wav", [0.5f, -0.5f, 0.25f], channels: 2, sampleRate: 44100);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ReadWavAsFloat_IntegerSource_Should_Normalize_Against_Its_Own_Bit_Depth()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, interleavedSamples: [32767, -32768, 0]);

                var (samples, channels, sampleRate) = AudioCutter.ReadWavAsFloat(sourcePath);

                channels.Should().Be(1);
                sampleRate.Should().Be(8000);
                samples[0].Should().BeApproximately(1.0f, 0.0001f);
                samples[1].Should().BeApproximately(-1.0f, 0.0001f);
                samples[2].Should().Be(0.0f);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            // Closes the "int PCM -> float WAV through the generic Convert API" gap: previously the only
            // way to get a float destination was the float-specific WriteWavFromFloat entry point.
            // Float32 requires a 32-bit source (WavWriter.Create enforces this -- see
            // Convert_WithFloat32Destination_NonThirtyTwoBitSource_Should_Throw for the mismatched case);
            // use a BitDepthFormatTransform pipeline to widen a non-32-bit source first.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 32, interleavedSamples: [1_000_000_000, -1_000_000_000, 0]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[3];
                reader.ReadInterleavedSamples(buffer, 3);
                buffer[0].Should().BeCloseTo(1_000_000_000, 5); // re-quantized through actual float32 storage
                buffer[2].Should().Be(0);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithFloat32Destination_NonThirtyTwoBitSource_Should_Throw()
        {
            // Float32 is only valid at 32-bit (see WavWriter.Create); a caller converting a non-32-bit
            // source must widen it first (e.g. with a BitDepthFormatTransform pipeline).
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 4, sampleRate: 8000); // 16-bit
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var act = () => AudioCutter.Convert(sourcePath, destPath, WavSampleFormat.Float32);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithFloat32Destination_ToNonWavExtension_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 4, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                var act = () => AudioCutter.Convert(sourcePath, destPath, WavSampleFormat.Float32);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_FloatWavSource_WithDefaultIntegerDestination_Should_Decode_Transparently()
        {
            // The other half of the format-boundary gap: a float WAV *source* has always decoded
            // transparently into int PCM (WavReader.IsFloatFormat handles this on read); this locks that
            // behavior in through the generic Convert API's default (Integer) destination format.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.CreateFloat32(sourcePath, channels: 1, sampleRate: 8000, [0.5f, -0.5f, 1.0f]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeFalse();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[3];
                reader.ReadInterleavedSamples(buffer, 3);
                buffer[0].Should().Be((int)(0.5 * int.MaxValue));
                buffer[2].Should().Be(int.MaxValue);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WithFloat32DestinationOption_Should_Write_A_Float_Wav()
        {
            // Float32 requires a 32-bit source (WavWriter.Create enforces this); CreateRampWav is
            // 16-bit, so widen it first via a BitDepthFormatTransform.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 10, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var produced = AudioCutter.Cut(sourcePath, destPath, startInSeconds: 0, endInSeconds: 1, new CutOptions
                {
                    Pipeline = new PcmTransformPipeline(new BitDepthFormatTransform(fromBits: 16, toBits: 32)),
                    DestinationWavFormat = WavSampleFormat.Float32
                });

                produced.Should().BeTrue();
                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            // Float32 requires a 32-bit source (WavWriter.Create enforces this); Mix has no bit-depth
            // conversion of its own, so the sources must already be 32-bit.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 32, interleavedSamples: [1_000_000_000, 1_000_000_000]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 1000, bitsPerSample: 32, interleavedSamples: [500_000_000, -1_500_000_000]);
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                AudioCutter.Mix([new MixInput(firstPath), new MixInput(secondPath, Gain: 0.5)], destPath, WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Concatenate_WithFloat32Destination_Should_Write_A_Float_Wav()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 32, interleavedSamples: [1, 2, 3]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 1000, bitsPerSample: 32, interleavedSamples: [4, 5]);
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                AudioCutter.Concatenate([firstPath, secondPath], destPath, WavSampleFormat.Float32);

                using var reader = WavReader.Open(destPath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.TotalSamples.Should().Be(5);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithChannelRemixTransform_Should_Change_Destination_Channel_Count()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, samples) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 10, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new ChannelRemixTransform(inputChannels: 2, outputChannels: 1)));

                using var reader = WavReader.Open(destPath);
                reader.Channels.Should().Be(1);
                reader.TotalSamples.Should().Be(10);

                var buffer = new int[10];
                reader.ReadInterleavedSamples(buffer, 10);
                // Source frames are (frame, -frame) per channel; the average of L+R is always 0.
                buffer.Should().OnlyContain(sample => sample == 0);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WhenPipelineRejectsTheSourceFormat_Should_Not_Create_A_Destination_File()
        {
            // Regression test: the destination sink is only opened after the first successful
            // pipeline.Apply() call. Previously it was opened first (writing a WAV header up front), so a
            // pipeline validation failure on the very first block -- e.g. a ChannelRemixTransform built
            // for the wrong input channel count -- left a corrupt, header-only WAV file behind instead of
            // no file at all.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 10, sampleRate: 1000); // 2 channels
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var mismatchedPipeline = new PcmTransformPipeline(new ChannelRemixTransform(inputChannels: 1, outputChannels: 2));
                var act = () => AudioCutter.Convert(sourcePath, destPath, mismatchedPipeline);

                act.Should().Throw<ArgumentException>();
                File.Exists(destPath).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithResamplingTransform_Should_Flush_The_Resamplers_Tail_Into_The_Destination()
        {
            // Without draining the resampler's pending tail on Flush, a source small enough to fit in one
            // decode block but too small to fill the filter's lookahead window would silently produce
            // fewer output frames than round(sourceFrames * ratio) -- or, for a source much smaller than
            // the filter's half-width, none at all.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 2));
                AudioCutter.Convert(sourcePath, destPath, pipeline);

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(10); // round(5 * 2.0), entirely from Flush since 5 frames can't fill the filter's lookahead
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WithResamplingTransform_Should_Flush_The_Resamplers_Tail_Into_The_Destination()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 5, sampleRate: 1000);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var produced = AudioCutter.Cut(sourcePath, destPath, startInSeconds: 0, endInSeconds: 1, new CutOptions
                {
                    Pipeline = new PcmTransformPipeline(new ResamplingTransform(sourceRate: 1000, targetRate: 2000, channels: 2))
                });

                produced.Should().BeTrue();
                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(10);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithBitDepthFormatTransform_Should_Change_Destination_Bit_Depth()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, interleavedSamples: [25600, -25600]);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new BitDepthFormatTransform(fromBits: 16, toBits: 24)));

                using var reader = WavReader.Open(destPath);
                reader.BitsPerSample.Should().Be(24);

                var buffer = new int[2];
                reader.ReadInterleavedSamples(buffer, 2);
                buffer.Should().Equal(25600 << 8, -25600 << 8);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WithFadeOptions_Should_Ramp_Start_And_End_Of_Retained_Range()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var constantSamples = Enumerable.Repeat(1000, 10).ToArray();
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 10, bitsPerSample: 16, interleavedSamples: constantSamples);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                // Cut the whole 1-second file (10 frames at 10Hz) with a 0.4s fade-in and 0.4s fade-out
                // (4 frames each, over a 10-frame retained range).
                var produced = AudioCutter.Cut(sourcePath, destPath, startInSeconds: 0, endInSeconds: 1, new CutOptions
                {
                    FadeInSeconds = 0.4,
                    FadeOutSeconds = 0.4
                });

                produced.Should().BeTrue();

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(10);

                var buffer = new int[10];
                reader.ReadInterleavedSamples(buffer, 10);

                buffer[0].Should().Be(0);
                buffer[9].Should().Be(0);
                buffer[4].Should().Be(1000); // middle, outside both fade windows, untouched
                buffer[1].Should().BeLessThan(buffer[2]).And.BeGreaterThan(0); // ramping up
                buffer[8].Should().BeLessThan(buffer[7]); // ramping down
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_WithFadeAndPipeline_Should_Apply_Both()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var constantSamples = Enumerable.Repeat(1000, 4).ToArray();
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 4, bitsPerSample: 16, interleavedSamples: constantSamples);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Cut(sourcePath, destPath, startInSeconds: 0, endInSeconds: 1, new CutOptions
                {
                    FadeInSeconds = 1.0,
                    Pipeline = new PcmTransformPipeline(new VolumeTransform(2.0))
                });

                using var reader = WavReader.Open(destPath);
                var buffer = new int[4];
                reader.ReadInterleavedSamples(buffer, 4);

                // Fade-in over the full 4-frame range (gains 0, .25, .5, .75) then doubled by the pipeline.
                buffer.Should().Equal(0, 500, 1000, 1500);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Cut_OutsideRange_Should_Return_False_And_Not_Create_A_File()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 10, sampleRate: 10);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                var produced = AudioCutter.Cut(sourcePath, destPath, startInSeconds: 5, endInSeconds: 10, new CutOptions { FadeInSeconds = 0.1 });

                produced.Should().BeFalse();
                File.Exists(destPath).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_TwoSources_Should_Sum_With_Gain()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [10000, 10000]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [5000, -30000]);
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                AudioCutter.Mix([new MixInput(firstPath, Gain: 1.0), new MixInput(secondPath, Gain: 0.5)], destPath);

                using var reader = WavReader.Open(destPath);
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(2);

                var buffer = new int[2];
                reader.ReadInterleavedSamples(buffer, 2);
                // frame0: 10000*1.0 + 5000*0.5 = 12500. frame1: 10000*1.0 + -30000*0.5 = -5000.
                buffer.Should().Equal(12500, -5000);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_DifferentLengthSources_Should_Pad_Shorter_With_Silence()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var longPath = Path.Combine(tempDirectory, "long.wav");
                var shortPath = Path.Combine(tempDirectory, "short.wav");
                WavFileBuilder.Create(longPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [100, 200, 300, 400]);
                WavFileBuilder.Create(shortPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1000, 1000]);
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                AudioCutter.Mix([new MixInput(longPath), new MixInput(shortPath)], destPath);

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(4);

                var buffer = new int[4];
                reader.ReadInterleavedSamples(buffer, 4);
                buffer.Should().Equal(1100, 1200, 300, 400);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_MismatchedSampleRate_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 2000, bitsPerSample: 16, interleavedSamples: [1, 2]);
                var destPath = Path.Combine(tempDirectory, "mixed.wav");

                var act = () => AudioCutter.Mix([new MixInput(firstPath), new MixInput(secondPath)], destPath);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_DecodesInputsConcurrently_And_Surfaces_The_Original_Exception_Type()
        {
            // Regression guard: Mix decodes every input concurrently (Parallel.For), which would
            // otherwise let a faulted iteration's exception escape wrapped in AggregateException --
            // callers catching a specific exception type (e.g. FileNotFoundException) must still see
            // exactly that, not a wrapper.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var existingPath = Path.Combine(tempDirectory, "first.wav");
                WavFileBuilder.Create(existingPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2]);
                var missingPath = Path.Combine(tempDirectory, "missing.wav");

                var act = () => AudioCutter.Mix([new MixInput(existingPath), new MixInput(missingPath)], Path.Combine(tempDirectory, "mixed.wav"));

                act.Should().Throw<FileNotFoundException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_WithMultipleFailingInputs_Should_Surface_The_FirstByIndex_Failure_Deterministically()
        {
            // Regression test: concurrent decode must not let whichever input's task happens to fault
            // first (an artifact of thread-pool scheduling) determine which exception surfaces --
            // matching the old sequential behavior, which always failed on the first bad input by index.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var existingPath = Path.Combine(tempDirectory, "first.wav");
                WavFileBuilder.Create(existingPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2]);
                var missingPath = Path.Combine(tempDirectory, "missing.wav"); // index 1: FileNotFoundException
                var unsupportedExtensionPath = Path.Combine(tempDirectory, "third.xyz"); // index 2: NotSupportedException

                var act = () => AudioCutter.Mix(
                    [new MixInput(existingPath), new MixInput(missingPath), new MixInput(unsupportedExtensionPath)],
                    Path.Combine(tempDirectory, "mixed.wav"));

                // Repeated to make a scheduling-order-dependent failure (i.e. the bug this guards against)
                // very unlikely to pass by chance.
                for (var i = 0; i < 20; i++)
                {
                    act.Should().Throw<FileNotFoundException>();
                }
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Mix_FewerThanTwoInputs_Should_Throw()
        {
            var act = () => AudioCutter.Mix([new MixInput("only.wav")], "dest.wav");

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Concatenate_TwoSources_Should_Preserve_Order_And_Total_Length()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2, 3]);
                WavFileBuilder.Create(secondPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [4, 5]);
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                AudioCutter.Concatenate([firstPath, secondPath], destPath);

                using var reader = WavReader.Open(destPath);
                reader.TotalSamples.Should().Be(5);

                var buffer = new int[5];
                reader.ReadInterleavedSamples(buffer, 5);
                buffer.Should().Equal(1, 2, 3, 4, 5);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Concatenate_MismatchedChannelCount_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var firstPath = Path.Combine(tempDirectory, "first.wav");
                var secondPath = Path.Combine(tempDirectory, "second.wav");
                WavFileBuilder.Create(firstPath, channels: 1, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2]);
                WavFileBuilder.Create(secondPath, channels: 2, sampleRate: 1000, bitsPerSample: 16, interleavedSamples: [1, 2, 3, 4]);
                var destPath = Path.Combine(tempDirectory, "concat.wav");

                var act = () => AudioCutter.Concatenate([firstPath, secondPath], destPath);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Concatenate_FewerThanTwoSources_Should_Throw()
        {
            var act = () => AudioCutter.Concatenate(["only.wav"], "dest.wav");

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Convert_WithG711Destination_Should_Apply_Pipeline_Transform_Before_Encoding(WavSampleFormat sampleFormat)
        {
            // Unlike IMA ADPCM (decode-only, so a pipeline could only ever run with it as the
            // SOURCE), G.711 supports the encode direction too -- this proves a PcmTransform
            // genuinely runs before the companding step, not just that the generic WavSampleFormat
            // destination routing (already proven for Float32) happens to compile for G.711 too.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.Combine(tempDirectory, "source.wav");
                var samples = new[] { 1000, -1000, 2000, -2000 };
                WavFileBuilder.Create(sourcePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, samples);
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)), sampleFormat);

                using var reader = WavReader.Open(destPath);
                reader.IsMuLaw.Should().Be(sampleFormat == WavSampleFormat.MuLaw);
                reader.IsALaw.Should().Be(sampleFormat == WavSampleFormat.ALaw);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Compare against the doubled samples companded directly, not the original samples --
                // if the gain transform were silently skipped, this comparison would fail.
                var expected = samples.Select(sample => sampleFormat == WavSampleFormat.MuLaw
                    ? G711Codec.DecodeMuLaw(G711Codec.EncodeMuLaw(sample * 2))
                    : G711Codec.DecodeALaw(G711Codec.EncodeALaw(sample * 2))).ToArray();

                buffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Convert_WithG711Destination_ToNonWavExtension_Should_Throw(WavSampleFormat sampleFormat)
        {
            // OpenSink's own validation widened from "== Float32" to "!= Integer" to cover G.711 too
            // -- confirms MuLaw/ALaw actually trigger it (not just Float32, the only value the
            // pre-existing test for this line exercised) before ever reaching a destination encoder.
            var tempDirectory = CreateTempDirectory();
            try
            {
                var (sourcePath, _) = CreateRampWav(tempDirectory, "source.wav", totalFrames: 4, sampleRate: 8000);
                var destPath = Path.Combine(tempDirectory, "dest.flac");

                var act = () => AudioCutter.Convert(sourcePath, destPath, sampleFormat);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Convert_WithMsAdpcmSource_Should_Apply_Pipeline_Transform_Before_Writing()
        {
            // MS ADPCM is decode-only (like IMA ADPCM), so it can only ever be the pipeline's SOURCE,
            // never its destination -- this proves a PcmTransform genuinely runs on its decoded
            // output, not just that an MS ADPCM source happens to be readable at all (already proven
            // by AudioCutterTest's own bit-exact decode coverage).
            var tempDirectory = CreateTempDirectory();
            try
            {
                var sourcePath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav");
                var destPath = Path.Combine(tempDirectory, "dest.wav");

                AudioCutter.Convert(sourcePath, destPath, new PcmTransformPipeline(new VolumeTransform(2.0)));

                using var sourceReader = WavReader.Open(sourcePath);
                var sourceBuffer = new int[sourceReader.TotalSamples];
                sourceReader.ReadInterleavedSamples(sourceBuffer, (int)sourceReader.TotalSamples);

                using var destReader = WavReader.Open(destPath);
                destReader.TotalSamples.Should().Be(sourceReader.TotalSamples);

                var destBuffer = new int[destReader.TotalSamples];
                destReader.ReadInterleavedSamples(destBuffer, (int)destReader.TotalSamples);

                // Compare against the source doubled directly, not the source as-is -- if the gain
                // transform were silently skipped, this comparison would fail. Clamp the same way
                // VolumeTransform itself does (int16 native range, since the source decodes to 16-bit).
                var expected = sourceBuffer.Select(s => Math.Clamp(s * 2, short.MinValue, short.MaxValue)).ToArray();
                destBuffer.Should().Equal(expected);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
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
