using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavWriterTest
    {
        [Fact]
        public void WriteInterleavedSamples_8Bit_Should_Round_Trip_Through_WavReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 127, -128, -64 };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_32Bit_Should_Round_Trip_Through_WavReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Float_Should_Round_Trip_Through_WavReader_As_Float()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // Values chosen to round-trip exactly through actual IEEE-float bytes on disk: 0 and
                // +/-int.MaxValue map to +/-1.0f exactly (see WavWriter.Int32ToFloat32 / WavReader.Float32ToInt32).
                // Arbitrary large magnitudes (e.g. int.MinValue) are not bit-exact through float32 storage --
                // that's expected precision loss from the format, not tested here.
                var samples = new[] { 0, int.MaxValue, -int.MaxValue };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, WavSampleFormat.Float32))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                reader.IsFloatFormat.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_FloatFormat_With_NonThirtyTwoBit_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 1, WavSampleFormat.Float32);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void Create_G711_With_NonSixteenBit_Should_Throw(WavSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: 1, sampleFormat);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void WriteInterleavedSamples_G711_With_FiveChannels_Should_Round_Trip_Without_ChannelCount_Restriction(WavSampleFormat sampleFormat)
        {
            // Deliberately NOT restricted to mono/stereo, unlike every other codec in this project --
            // G.711 has no block/frame structure or adaptive state, so any channel count decodes and
            // encodes correctly. Confirms this actually works end to end, not just that neither
            // WavWriter.Create nor WavReader.Open happens to contain a channel-count check for it.
            var filePath = Path.GetTempFileName();
            try
            {
                const int channels = 5;
                const int frameCount = 3;
                var samples = new int[frameCount * channels];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (i * 1000) - 7000; // spread across the range, including negative values
                }

                var expected = samples.Select(sample => sampleFormat == WavSampleFormat.MuLaw
                    ? G711Codec.DecodeMuLaw(G711Codec.EncodeMuLaw(sample))
                    : G711Codec.DecodeALaw(G711Codec.EncodeALaw(sample))).ToArray();

                using (var writer = WavWriter.Create(filePath, channels, sampleRate: 8000, bitsPerSample: 16, totalFrames: frameCount, sampleFormat))
                {
                    writer.WriteInterleavedSamples(samples, frameCount);
                }

                using var reader = WavReader.Open(filePath);
                reader.Channels.Should().Be(channels);
                reader.TotalSamples.Should().Be(frameCount);

                var buffer = new int[samples.Length];
                var framesRead = reader.ReadInterleavedSamples(buffer, frameCount);

                framesRead.Should().Be(frameCount);
                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw)]
        [InlineData(WavSampleFormat.ALaw)]
        public void WriteInterleavedSamples_G711_Should_Round_Trip_Through_WavReader(WavSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 32767, -32768, 1000, -1000, 12345, -12345 };
                var expected = samples.Select(sample => sampleFormat == WavSampleFormat.MuLaw
                    ? G711Codec.DecodeMuLaw(G711Codec.EncodeMuLaw(sample))
                    : G711Codec.DecodeALaw(G711Codec.EncodeALaw(sample))).ToArray();

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: samples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = WavReader.Open(filePath);
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(samples.Length);
                reader.IsMuLaw.Should().Be(sampleFormat == WavSampleFormat.MuLaw);
                reader.IsALaw.Should().Be(sampleFormat == WavSampleFormat.ALaw);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(WavSampleFormat.MuLaw, "sample_g711_mulaw_mono.wav", "sample_g711_mulaw_mono_expected.pcm")]
        [InlineData(WavSampleFormat.ALaw, "sample_g711_alaw_mono.wav", "sample_g711_alaw_mono_expected.pcm")]
        public void WriteInterleavedSamples_G711_Should_Produce_BitExact_Bytes_Against_FfmpegEncodedFixture(WavSampleFormat sampleFormat, string fixtureFileName, string expectedPcmFileName)
        {
            // The strongest possible encode check: feed ffmpeg's own ground-truth decoded samples
            // back through this project's encoder and confirm the resulting coded BYTES match
            // ffmpeg's own real encoder output exactly, byte for byte -- not just that our own
            // decode(encode(x)) composes correctly in isolation (already covered by G711CodecTest).
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPcmPath = Path.GetFullPath($"Codecs/Wav/{expectedPcmFileName}");
            var groundTruthSamples = ReadGroundTruthPcm16(expectedPcmPath);

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 16, totalFrames: groundTruthSamples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(groundTruthSamples, groundTruthSamples.Length);
                }

                var producedDataBytes = ReadDataChunkBytes(filePath);
                var realEncodedDataBytes = ReadDataChunkBytes(fixturePath);

                producedDataBytes.Should().Equal(realEncodedDataBytes);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_ImaAdpcm_With_NonSixteenBit_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: 1, WavSampleFormat.ImaAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void Create_ImaAdpcm_WithUnsupportedChannelCount_Should_Throw(int channels)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1, WavSampleFormat.ImaAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_Mono_Should_Round_Trip_Through_WavReader_WithinQuantizationTolerance()
        {
            // IMA ADPCM is inherently lossy -- unlike every other WavSampleFormat test above, exact
            // equality is the wrong bar. A smooth, slowly-varying signal (ImaAdpcmDecoderTest's own
            // QuantizeNibble_CalledRepeatedly_Should_Track_A_Slowly_Varying_Signal_Closely already
            // proves the quantizer itself tracks such a signal within a few thousand units once
            // adapted) going through the real block-structured WavWriter -> WavReader round trip,
            // including a header reset at every block boundary, should do the same.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[500];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(i * 0.05));
                }

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.ImaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.IsImaAdpcm.Should().BeTrue();
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Skip each block's own first frame: that one is a verbatim, bit-exact header sample
                // by construction (see ImaAdpcmEncoder), so including it would understate the real
                // quantization tolerance being exercised here.
                for (var i = 1; i < samples.Length; i++)
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(3000, $"sample {i} should reconstruct within a reasonable quantization tolerance");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_Stereo_Should_Keep_Channels_Independent()
        {
            // Two genuinely different per-channel signals (not the degenerate L==R case the real
            // fixture used elsewhere in this project turned out to have) -- confirms the encoder's
            // own per-channel ChannelState array and block-header layout don't cross-contaminate.
            var filePath = Path.GetTempFileName();
            try
            {
                const int frameCount = 300;
                var samples = new int[frameCount * 2];
                for (var i = 0; i < frameCount; i++)
                {
                    samples[i * 2] = (int)(8000 * Math.Sin(i * 0.05));
                    samples[(i * 2) + 1] = (int)(4000 * Math.Cos(i * 0.1));
                }

                using (var writer = WavWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: frameCount, WavSampleFormat.ImaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, frameCount);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(frameCount);

                var buffer = new int[frameCount * 2];
                reader.ReadInterleavedSamples(buffer, frameCount);

                for (var i = 2; i < samples.Length; i++) // skip frame 0's verbatim header samples
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(3000);
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_WithFrameCountNotMultipleOfSamplesPerBlock_Should_PadFinalBlock()
        {
            // The IMA ADPCM block size this encoder uses (1024-byte block align) gives 2041
            // samples/block for mono -- deliberately using far fewer frames than that here, so the
            // only block written is a short, padded one. Confirms Finish() actually flushes the
            // pending partial block (nothing else would trigger a write at all for this few frames)
            // and that the 'fact' chunk's true count, not the padded on-disk block's capacity,
            // is what TotalSamples reports back.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 37).Select(i => i * 100).ToArray();

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.ImaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                var framesRead = reader.ReadInterleavedSamples(buffer, samples.Length);

                framesRead.Should().Be(samples.Length);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_SpanningMultipleBlocks_Should_Reset_Predictor_At_Each_Block_Boundary()
        {
            // Every other test above uses fewer frames than one mono block (2041 samples, derived from
            // the 1024-byte block align the same way WavWriter.CreateImaAdpcm does), so none of them
            // can actually exercise what happens at a SECOND block's own boundary -- confirmed by
            // mutation: temporarily removing ImaAdpcmEncoder's own per-block predictor reset left every
            // other test in this file passing, including the real-ffmpeg bit-exact cross-check (decode
            // always reads each block's header verbatim regardless of what the encoder's own internal
            // state was, so two independent DECODERS agreeing with each other proves nothing about
            // whether the ENCODER's quantizer choices were actually sound).
            //
            // Uses a genuine discontinuity exactly at the block boundary: silence for the whole of
            // block 0, then a large, sustained jump starting at block 1's own first sample. If the
            // predictor correctly resets to block 1's own true first raw sample (always written
            // verbatim into that block's header, regardless), the quantizer should track the jump
            // almost immediately; if it doesn't (carrying over block 0's near-zero ending state
            // instead, the mutation above), the step size left over from block 0's own near-silent
            // adaptation is also still small, so even with step size growing quickly under repeated
            // maximum-magnitude nibbles, sample 6 of block 1 measured 533 off target under the mutation
            // -- confirmed by actually running it, not assumed -- just over this test's own 500 bound.
            const int samplesPerBlockMono = 2041;
            var samples = new int[samplesPerBlockMono + 50];
            for (var i = samplesPerBlockMono; i < samples.Length; i++)
            {
                samples[i] = 12000;
            }

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.ImaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer[samplesPerBlockMono].Should().Be(12000, "block 1's own header predictor is always its own raw first sample, verbatim, regardless of the encoder's internal state");

                for (var i = samplesPerBlockMono + 1; i < samplesPerBlockMono + 10; i++)
                {
                    Math.Abs(buffer[i] - 12000).Should().BeLessThan(500, $"sample {i}, early in block 1, should already track the jump closely -- only possible if the predictor was reset to block 1's own first sample at the block boundary, not left near block 0's own ending state");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_WithoutCallingFinish_Should_Not_Write_The_Pending_Partial_Block()
        {
            // Finish() is what flushes a short final block (see the test above) -- confirms that
            // without it, a partial block that never filled is never physically written: the file's
            // actual length on disk stays at just the header's own size, even though the header's own
            // 'data' chunk size field already committed to one full block's worth of space (it's
            // computed from the requested totalFrames up front in Create(), independent of whatever
            // Finish() later actually flushes).
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 10).Select(i => i * 100).ToArray();
                long headerOnlyLength;

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.ImaAdpcm))
                {
                    headerOnlyLength = new FileInfo(filePath).Length;
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                new FileInfo(filePath).Length.Should().Be(headerOnlyLength, "the pending block was never flushed, so no bytes should have been appended after the header");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_WithZeroFrames_Should_Produce_An_Empty_DataChunk()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 0, WavSampleFormat.ImaAdpcm))
                {
                    writer.Finish();
                }

                var dataBytes = ReadDataChunkBytes(filePath);
                dataBytes.Should().BeEmpty();

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_ImaAdpcm_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw()
        {
            // End-to-end version of ImaAdpcmDecoderTest's own
            // QuantizeNibble_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw -- confirms the full
            // public WavWriter.Create + WriteInterleavedSamples path (reachable directly, without going
            // through AudioCutter's own pipeline, whose transforms are the only thing conventionally
            // guaranteeing an in-range value) doesn't crash either, not just the internal quantizer
            // method in isolation.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[10];
                samples[1] = int.MinValue;
                samples[5] = int.MaxValue;

                var act = () =>
                {
                    using var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.ImaAdpcm);
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("ima_adpcm_encoded_mono.wav", "ima_adpcm_encoded_mono_expected.pcm")]
        [InlineData("ima_adpcm_encoded_stereo.wav", "ima_adpcm_encoded_stereo_expected.pcm")]
        public void WriteInterleavedSamples_ImaAdpcm_FixtureEncodedByThisProject_Should_Decode_BitExact_Against_RealFfmpegsIndependentDecode(string fixtureFileName, string expectedPcmFileName)
        {
            // The strongest real-world interoperability check available for a lossy, block-structured
            // encoder: both fixture files here were produced by THIS project's own WavWriter (checked
            // in exactly as generated, not regenerated at test time), then fed through real ffmpeg once
            // during this feature's own development -- ffmpeg correctly identified both as genuine
            // adpcm_ima_wav and decoded them (codec_name=adpcm_ima_wav, confirmed via ffprobe); the
            // expected .pcm files here are ffmpeg's own decode of those exact bytes. Since decode itself
            // is a deterministic, standardized nibble-expansion formula (not a quantizer's subjective
            // choice the way encode is), this project's own WavReader decode of the identical bytes
            // must match ffmpeg's independent decode bit-exactly over every genuinely meaningful sample
            // -- not just within some lossy tolerance -- or one of the two decoders disagrees with the
            // real standard. (ffmpeg decodes every physical block, including this file's own padded
            // final block beyond the 'fact' chunk's true count; only the true, meaningful prefix is
            // compared here.)
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Wav/{expectedPcmFileName}");
            var ffmpegDecoded = ReadGroundTruthPcm16(expectedPath);

            using var reader = WavReader.Open(fixturePath);
            reader.IsImaAdpcm.Should().BeTrue();

            var buffer = new int[reader.TotalSamples * reader.Channels];
            reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

            buffer.Should().Equal(ffmpegDecoded[..buffer.Length]);
        }

        [Fact]
        public void Create_YamahaAdpcm_With_NonSixteenBit_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: 1, WavSampleFormat.YamahaAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void Create_YamahaAdpcm_WithUnsupportedChannelCount_Should_Throw(int channels)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1, WavSampleFormat.YamahaAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_Mono_Should_Round_Trip_Through_WavReader_WithinQuantizationTolerance()
        {
            // Yamaha ADPCM is inherently lossy, same as IMA/MS ADPCM -- a smooth, slowly-varying signal
            // (YamahaAdpcmEncoderTest's own CompressSample_CalledRepeatedly_Should_Track_A_Slowly_
            // Varying_Signal_Closely already proves the quantizer itself tracks such a signal within a
            // few thousand units once adapted) going through the real WavWriter -> WavReader round
            // trip should do the same. Unlike IMA/MS ADPCM, there's no per-block header to skip here --
            // every single sample, including the first, goes through the same continuous quantizer.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[500];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(i * 0.05));
                }

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.YamahaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.IsYamahaAdpcm.Should().BeTrue();
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                for (var i = 0; i < samples.Length; i++)
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(3000, $"sample {i} should reconstruct within a reasonable quantization tolerance");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_Stereo_Should_Keep_Channels_Independent()
        {
            // Two genuinely different per-channel signals (different frequency and amplitude) --
            // confirms the encoder's own per-channel ChannelState array indexing (channel = sample
            // index % channels) doesn't cross-contaminate. Both start at 0 and ramp up smoothly
            // (deliberately NOT using e.g. a cosine, which starts at its own peak amplitude): unlike
            // IMA/MS ADPCM, Yamaha ADPCM has no verbatim first-sample header at all, so every sample
            // -- including the first -- goes through the quantizer from its own cold-start step floor
            // (127); a signal that starts already at a large amplitude would need several samples
            // just to ramp the adaptive step up before it can track at all, which is a real, inherent
            // property of this format (confirmed bit-exact against ffmpeg's own independent decode
            // elsewhere in this file), not something a channel-independence test should be measuring.
            var filePath = Path.GetTempFileName();
            try
            {
                const int frameCount = 300;
                var samples = new int[frameCount * 2];
                for (var i = 0; i < frameCount; i++)
                {
                    samples[i * 2] = (int)(8000 * Math.Sin(i * 0.05));
                    samples[(i * 2) + 1] = (int)(4000 * Math.Sin(i * 0.1));
                }

                using (var writer = WavWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: frameCount, WavSampleFormat.YamahaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, frameCount);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(frameCount);

                var buffer = new int[frameCount * 2];
                reader.ReadInterleavedSamples(buffer, frameCount);

                for (var i = 0; i < samples.Length; i++)
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(3000);
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_WithOddMonoFrameCount_Should_PadTheTrailingNibble()
        {
            // Yamaha ADPCM has no block structure, so unlike IMA/MS ADPCM there's no "pad the final
            // block" concern -- but mono's own 2-samples-per-byte packing means an ODD total frame
            // count leaves exactly one nibble pending at the very end, which Finish() must flush,
            // zero-padded, for the file to contain the true number of on-disk nibbles.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 37).Select(i => i * 100).ToArray(); // odd frame count

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.YamahaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                var framesRead = reader.ReadInterleavedSamples(buffer, samples.Length);

                framesRead.Should().Be(samples.Length);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_WithoutCallingFinish_Should_Not_Write_The_Pending_Nibble()
        {
            // Mirrors WriteInterleavedSamples_ImaAdpcm_WithoutCallingFinish_Should_Not_Write_The_
            // Pending_Partial_Block's own role for IMA ADPCM's block buffer, just for the one pending
            // nibble an odd mono frame count leaves behind instead of a whole block. Unlike that IMA
            // ADPCM test, WriteInterleavedSamples here DOES write real bytes to the stream before
            // Finish() (5 complete byte-pairs for 11 samples) rather than nothing at all, so a naive
            // "snapshot the length mid-stream, before Dispose" comparison is unreliable -- the
            // FileStream's own internal buffer may not have reached the OS yet at that point, only
            // becoming visible to a fresh FileInfo once Dispose() flushes and closes it. Both lengths
            // compared below are instead taken only AFTER their own stream's Dispose() has fully
            // flushed, from two otherwise-identical files (same channels/totalFrames, so the same
            // fixed header layout), so the only difference between them is the 5 real bytes actually
            // written.
            var filePath = Path.GetTempFileName();
            var headerOnlyPath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 11).Select(i => i * 100).ToArray(); // 11 nibbles -> 5 full bytes + 1 pending nibble

                using (WavWriter.Create(headerOnlyPath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.YamahaAdpcm))
                {
                    // Nothing written at all -- this file exists only to measure the fixed header size.
                }

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.YamahaAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    // Finish() deliberately not called -- the 11th sample's own nibble must stay pending.
                }

                var headerOnlyLength = new FileInfo(headerOnlyPath).Length;
                new FileInfo(filePath).Length.Should().Be(headerOnlyLength + 5, "5 complete byte-pairs should have been written; the 11th sample's own nibble was left pending and never flushed since Finish() was never called");
            }
            finally
            {
                File.Delete(filePath);
                File.Delete(headerOnlyPath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_WithZeroFrames_Should_Produce_An_Empty_DataChunk()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 0, WavSampleFormat.YamahaAdpcm))
                {
                    writer.Finish();
                }

                var dataBytes = ReadDataChunkBytes(filePath);
                dataBytes.Should().BeEmpty();

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_YamahaAdpcm_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw()
        {
            // End-to-end version of YamahaAdpcmEncoderTest's own
            // CompressSample_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw -- confirms the full
            // public WavWriter.Create + WriteInterleavedSamples path doesn't crash either, not just the
            // internal quantizer method in isolation.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[10];
                samples[1] = int.MinValue;
                samples[5] = int.MaxValue;

                var act = () =>
                {
                    using var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.YamahaAdpcm);
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("yamaha_adpcm_encoded_mono.wav", "yamaha_adpcm_encoded_mono_expected.pcm")]
        [InlineData("yamaha_adpcm_encoded_stereo.wav", "yamaha_adpcm_encoded_stereo_expected.pcm")]
        public void WriteInterleavedSamples_YamahaAdpcm_FixtureEncodedByThisProject_Should_Decode_BitExact_Against_RealFfmpegsIndependentDecode(string fixtureFileName, string expectedPcmFileName)
        {
            // Mirrors WriteInterleavedSamples_ImaAdpcm_FixtureEncodedByThisProject_...'s own role for
            // this format: both fixture files here were produced by THIS project's own WavWriter
            // (checked in exactly as generated, not regenerated at test time), then fed through real
            // ffmpeg once during this feature's own development -- ffmpeg correctly identified both as
            // genuine adpcm_yamaha and decoded them (codec_name=adpcm_yamaha, confirmed via ffprobe);
            // the expected .pcm files here are ffmpeg's own decode of those exact bytes. Unlike IMA
            // ADPCM's own analogous test, this format has no block padding at all -- the data chunk's
            // size is exactly ceil(totalFrames * channels / 2) bytes, so ffmpeg's own decode is exactly
            // totalFrames long with nothing to trim.
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Wav/{expectedPcmFileName}");
            var ffmpegDecoded = ReadGroundTruthPcm16(expectedPath);

            using var reader = WavReader.Open(fixturePath);
            reader.IsYamahaAdpcm.Should().BeTrue();

            var buffer = new int[reader.TotalSamples * reader.Channels];
            reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

            buffer.Should().Equal(ffmpegDecoded);
        }

        [Fact]
        public void Create_MsAdpcm_With_NonSixteenBit_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: 1, WavSampleFormat.MsAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void Create_MsAdpcm_WithUnsupportedChannelCount_Should_Throw(int channels)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavWriter.Create(filePath, channels, sampleRate: 8000, bitsPerSample: 16, totalFrames: 1, WavSampleFormat.MsAdpcm);

                act.Should().Throw<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_Mono_Should_Round_Trip_Through_WavReader_WithinQuantizationTolerance()
        {
            // MS ADPCM is inherently lossy -- exact equality is the wrong bar, the same reasoning
            // already applied to IMA ADPCM's own round-trip tests above.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[500];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (int)(8000 * Math.Sin(i * 0.05));
                }

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.MsAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.IsMsAdpcm.Should().BeTrue();
                reader.BitsPerSample.Should().Be(16);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Skip each block's own first two frames: those are verbatim, bit-exact header
                // samples by construction (see MsAdpcmEncoder), so including them would understate
                // the real quantization tolerance being exercised here.
                for (var i = 2; i < samples.Length; i++)
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(2000, $"sample {i} should reconstruct within a reasonable quantization tolerance");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_Stereo_Should_Keep_Channels_Independent()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                const int frameCount = 300;
                var samples = new int[frameCount * 2];
                for (var i = 0; i < frameCount; i++)
                {
                    samples[i * 2] = (int)(8000 * Math.Sin(i * 0.05));
                    samples[(i * 2) + 1] = (int)(4000 * Math.Cos(i * 0.1));
                }

                using (var writer = WavWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: frameCount, WavSampleFormat.MsAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, frameCount);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.Channels.Should().Be(2);
                reader.TotalSamples.Should().Be(frameCount);

                var buffer = new int[frameCount * 2];
                reader.ReadInterleavedSamples(buffer, frameCount);

                for (var i = 4; i < samples.Length; i++) // skip both channels' verbatim header frames (frame 0 and frame 1)
                {
                    Math.Abs(buffer[i] - samples[i]).Should().BeLessThan(2000);
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_WithFrameCountNotMultipleOfSamplesPerBlock_Should_PadFinalBlock()
        {
            // The MS ADPCM block size this encoder uses (1024-byte block align) gives 2036
            // samples/block for mono -- deliberately using far fewer frames than that here, so the
            // only block written is a short, padded one.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 37).Select(i => i * 100).ToArray();

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.MsAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(samples.Length);

                var buffer = new int[samples.Length];
                var framesRead = reader.ReadInterleavedSamples(buffer, samples.Length);

                framesRead.Should().Be(samples.Length);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void WriteInterleavedSamples_MsAdpcm_WithFewerThanTwoRealFrames_Should_Not_Throw(int frameCount)
        {
            // MS ADPCM's own block header needs TWO raw verbatim samples (Sample2, then Sample1) --
            // a genuinely distinct boundary condition from IMA ADPCM's own single-verbatim-sample
            // header: a file with only 1 real frame needs the padding loop to supply BOTH header
            // samples' worth of data (not just nibble-encoded ones), and even 2 real frames leaves
            // zero samples for the nibble-encoding loop to run at all. Confirmed via a real
            // WavWriter.Create + WriteInterleavedSamples call before writing this test, not assumed.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, frameCount).Select(i => (i + 1) * 100).ToArray();

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: frameCount, WavSampleFormat.MsAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, frameCount);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(frameCount);

                var buffer = new int[frameCount];
                var framesRead = reader.ReadInterleavedSamples(buffer, frameCount);

                framesRead.Should().Be(frameCount);
                buffer.Should().Equal(samples, "both header samples are written verbatim, so even a file this short should reconstruct bit-exactly");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_SpanningMultipleBlocks_Should_Reset_Sample1Sample2_At_Each_Block_Boundary()
        {
            // Mirrors WriteInterleavedSamples_ImaAdpcm_SpanningMultipleBlocks_Should_Reset_Predictor_
            // At_Each_Block_Boundary's own reasoning for IMA ADPCM: every round-trip/tolerance test
            // above uses fewer frames than one block (2036 samples/block for mono), so none of them
            // exercise what happens at a SECOND block's own boundary, and the real-ffmpeg bit-exact
            // cross-check doesn't catch a broken reset either (decode always reads each block's
            // header verbatim regardless of the encoder's own internal state). Uses a genuine
            // discontinuity exactly at the block boundary: silence for block 0, then a large,
            // sustained jump starting at block 1's own first sample.
            const int samplesPerBlockMono = 2036;
            var samples = new int[samplesPerBlockMono + 50];
            for (var i = samplesPerBlockMono; i < samples.Length; i++)
            {
                samples[i] = 12000;
            }

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.MsAdpcm))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                buffer[samplesPerBlockMono].Should().Be(12000, "block 1's own header sample2 is always its own raw first sample, verbatim");
                buffer[samplesPerBlockMono + 1].Should().Be(12000, "block 1's own header sample1 is always its own raw second sample, verbatim");

                for (var i = samplesPerBlockMono + 2; i < samplesPerBlockMono + 10; i++)
                {
                    Math.Abs(buffer[i] - 12000).Should().BeLessThan(500, $"sample {i}, early in block 1, should already track the jump closely -- only possible if Sample1/Sample2 were reset to block 1's own first two samples at the block boundary, not left near block 0's own ending state");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_WithoutCallingFinish_Should_Not_Write_The_Pending_Partial_Block()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = Enumerable.Range(0, 10).Select(i => i * 100).ToArray();
                long headerOnlyLength;

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.MsAdpcm))
                {
                    headerOnlyLength = new FileInfo(filePath).Length;
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                new FileInfo(filePath).Length.Should().Be(headerOnlyLength, "the pending block was never flushed, so no bytes should have been appended after the header");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_WithZeroFrames_Should_Produce_An_Empty_DataChunk()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 0, WavSampleFormat.MsAdpcm))
                {
                    writer.Finish();
                }

                var dataBytes = ReadDataChunkBytes(filePath);
                dataBytes.Should().BeEmpty();

                using var reader = WavReader.Open(filePath);
                reader.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_MsAdpcm_WithOutOfContractExtremeSample_Should_Clamp_Not_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new int[10];
                samples[1] = int.MinValue;
                samples[5] = int.MaxValue;

                var act = () =>
                {
                    using var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, WavSampleFormat.MsAdpcm);
                    writer.WriteInterleavedSamples(samples, samples.Length);
                    writer.Finish();
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("ms_adpcm_encoded_mono.wav", "ms_adpcm_encoded_mono_expected.pcm")]
        [InlineData("ms_adpcm_encoded_stereo.wav", "ms_adpcm_encoded_stereo_expected.pcm")]
        public void WriteInterleavedSamples_MsAdpcm_FixtureEncodedByThisProject_Should_Decode_BitExact_Against_RealFfmpegsIndependentDecode(string fixtureFileName, string expectedPcmFileName)
        {
            // Mirrors WriteInterleavedSamples_ImaAdpcm_FixtureEncodedByThisProject_...'s own role for
            // IMA ADPCM: both fixtures here were produced by THIS project's own WavWriter, then fed
            // through real ffmpeg once during this feature's own development -- ffmpeg correctly
            // identified both as genuine adpcm_ms and decoded them (confirmed via ffprobe); the
            // expected .pcm files are ffmpeg's own decode of those exact bytes. This project's own
            // WavReader decode of the identical bytes must match bit-exactly.
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Wav/{expectedPcmFileName}");
            var ffmpegDecoded = ReadGroundTruthPcm16(expectedPath);

            using var reader = WavReader.Open(fixturePath);
            reader.IsMsAdpcm.Should().BeTrue();

            var buffer = new int[reader.TotalSamples * reader.Channels];
            reader.ReadInterleavedSamples(buffer, (int)reader.TotalSamples);

            buffer.Should().Equal(ffmpegDecoded[..buffer.Length]);
        }

        [Fact]
        public void WriteInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var firstBlock = new[] { 1, 2, 3, 4, 5, 6 };
                var secondBlock = new[] { 7, 8 };

                using (var writer = WavWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: firstBlock.Length + secondBlock.Length))
                {
                    writer.WriteInterleavedSamples(firstBlock, firstBlock.Length);
                    writer.WriteInterleavedSamples(secondBlock, secondBlock.Length);
                }

                using var reader = WavReader.Open(filePath);
                var buffer = new int[firstBlock.Length + secondBlock.Length];
                reader.ReadInterleavedSamples(buffer, buffer.Length);

                buffer.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
            }
            finally
            {
                File.Delete(filePath);
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
            for (var i = 12; i <= bytes.Length - 8; i++)
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
    }
}
