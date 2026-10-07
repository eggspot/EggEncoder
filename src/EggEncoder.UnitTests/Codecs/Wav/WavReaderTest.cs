using EggEncoder.Codecs.Wav;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wav
{
    public class WavReaderTest
    {
        [Fact]
        public void Open_16Bit_Stereo_Should_Read_Header_And_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 0, short.MaxValue, short.MinValue, -1, 1 };
                WavFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);

                using var wavReader = WavReader.Open(filePath);

                wavReader.Channels.Should().Be(2);
                wavReader.SampleRate.Should().Be(44100);
                wavReader.BitsPerSample.Should().Be(16);
                wavReader.TotalSamples.Should().Be(3);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesRead.Should().Be(3);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_24Bit_Mono_Should_Sign_Extend_Negative_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 8388607, -8388608, -1 };
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, interleavedSamples);

                using var wavReader = WavReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_8Bit_Mono_Should_Convert_Unsigned_Bytes_To_Signed_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var unsignedBytes = new[] { 128, 255, 0, 64 };
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, unsignedBytes);

                using var wavReader = WavReader.Open(filePath);
                wavReader.BitsPerSample.Should().Be(8);

                var buffer = new int[unsignedBytes.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: unsignedBytes.Length);

                framesRead.Should().Be(unsignedBytes.Length);
                buffer.Should().Equal(0, 127, -128, -64);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32BitFloat_Stereo_Should_Scale_To_Full_Int32_Range()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var floatSamples = new[] { 0f, 1f, -1f, 0.5f };
                WavFileBuilder.CreateFloat32(filePath, channels: 2, sampleRate: 44100, floatSamples);

                using var wavReader = WavReader.Open(filePath);
                wavReader.BitsPerSample.Should().Be(32);
                wavReader.Channels.Should().Be(2);

                var buffer = new int[floatSamples.Length];
                var framesRead = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 2);

                framesRead.Should().Be(2);
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(-int.MaxValue);
                buffer[3].Should().Be(int.MaxValue / 2);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32BitFloat_NaN_Should_Decode_As_Silence()
        {
            // A plain (int)double.NaN cast's result for an out-of-range value is unspecified by the C#
            // spec, so relying on it (whatever it happens to evaluate to on a given runtime) would be
            // fragile. A malformed or synthesized float WAV could still contain a NaN sample, so this is
            // handled explicitly instead, mapping it to silence (0).
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.CreateFloat32(filePath, channels: 1, sampleRate: 44100, [float.NaN]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[1];
                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 1);

                buffer[0].Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(float.PositiveInfinity, int.MaxValue)]
        [InlineData(float.NegativeInfinity, -int.MaxValue)]
        public void Open_32BitFloat_Infinity_Should_Clamp_To_FullScale(float input, int expected)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.CreateFloat32(filePath, channels: 1, sampleRate: 44100, [input]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[1];
                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 1);

                buffer[0].Should().Be(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadInterleavedSamples_Should_Return_Zero_At_End_Of_Stream()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3]);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[3];

                wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);
                var framesReadAfterEnd = wavReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesReadAfterEnd.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_UnsupportedBitsPerSample_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 12, [0, 1]);

                var act = () => WavReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcmMono_Should_Decode_BitExact_Against_FfmpegAndCoreAudio_GroundTruth()
        {
            // The ground-truth .pcm was generated once by decoding a real ffmpeg-produced IMA ADPCM
            // file via two independent, real-world decoders (ffmpeg's own adpcm_ima_wav, and macOS's
            // afconvert/CoreAudio) that agreed on every one of 88200 samples -- this is a from-scratch
            // implementation of a well-standardized algorithm, not a third-party dependency, so
            // nothing here is "assumed correct" the way a native library's own behavior would be.
            //
            // This cross-check is exactly how a real, subtle bug was caught during development: an
            // earlier version of ImaAdpcmDecoder.ExpandNibble used a single-shift formula that looked
            // "algebraically identical" to the correct, separately-truncated one on paper, but wasn't
            // whenever step wasn't a multiple of 8 -- seeded obviously-wrong output almost immediately
            // (sample 2 of 88200 was already off by 2) once checked against real, independent decoders,
            // rather than trusting a hand-derived equivalence proof alone.
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.Channels.Should().Be(1);
            wavReader.SampleRate.Should().Be(44100);
            wavReader.BitsPerSample.Should().Be(16, "IMA ADPCM decodes to 16-bit PCM resolution regardless of its own 4-bit coded storage width");
            wavReader.TotalSamples.Should().Be(88200, "the file's own 'fact' chunk is the authoritative total, trimming the last block's trailing padding");

            var decoded = DecodeAll(wavReader);

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_ImaAdpcmStereo_Should_Decode_BitExact_Against_FfmpegGroundTruth()
        {
            // Same rationale as the mono case, but this is the one real file that exercises the
            // 4-byte-group-per-channel interleaving specifically (two different source tones, so a
            // channel-swap or cross-contamination bug would be immediately visible as wrong pitch/
            // phase in one channel, not just a subtly-off sample value).
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_stereo.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_stereo_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.Channels.Should().Be(2);
            wavReader.TotalSamples.Should().Be(88200);

            var decoded = DecodeAll(wavReader);

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_ImaAdpcm_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            // Each real block here decodes to far more samples (2041/channel) than a deliberately tiny
            // buffer can hold in one call -- this forces ReadInterleavedSamples' internal pending-sample
            // buffering to drain across many calls, including calls that land mid-block, exercising a
            // path the other tests (which use a comfortably large 4096-frame buffer) never reach.
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_ima_adpcm_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37]; // deliberately not a divisor of the 2041-sample block
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_ImaAdpcm_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 3, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2041, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_With_FmtChunkTooShortForExtension_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // A plain 16-byte fmt chunk (no cbSize/wSamplesPerBlock extension at all) declaring
                // format tag 17 -- structurally impossible to decode correctly since the block
                // structure can't be determined, so this must be rejected, not guessed at.
                WavImaAdpcmFileBuilder.CreateWithTruncatedFmtChunk(filePath);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_With_ZeroSamplesPerBlock_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 0, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_With_BlockAlignTooSmallForHeader_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // channels=1 needs a 4-byte header alone; blockAlign=3 can't even hold that.
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 3, samplesPerBlock: 2, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_With_SamplesPerBlockExceedingBlockAlignCapacity_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // channels=1, blockAlign=8 -> 4 header bytes + 4 data bytes (8 nibbles) can hold at
                // most 1 (header) + 8 = 9 samples; declaring 10 claims more than the block can supply.
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 8, samplesPerBlock: 10, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_WithoutFactChunk_Should_FallBack_To_BlockCountDerivedTotal()
        {
            // The 'fact' chunk is the preferred, authoritative source for TotalSamples, but it's not
            // structurally required by the format tag itself -- a file missing it entirely should
            // still decode using the block-count * samplesPerBlock formula instead of failing outright.
            var filePath = Path.GetTempFileName();
            try
            {
                // 2 full blocks, blockAlign=1024, samplesPerBlock=2041, no 'fact' chunk at all.
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2041, totalSamples: null, blockCount: 2);

                using var wavReader = WavReader.Open(filePath);

                wavReader.TotalSamples.Should().Be(2 * 2041);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_ImaAdpcm_WithTruncatedFinalBlock_Should_Stop_Without_Throwing()
        {
            // A genuinely truncated file (fewer bytes than one full block remaining) is a malformed
            // input this project doesn't try to recover partial data from -- ReadInterleavedSamples
            // should just stop cleanly (return 0) rather than throw or read out of bounds.
            var filePath = Path.GetTempFileName();
            try
            {
                WavImaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2041, totalSamples: 4082, blockCount: 2, truncateLastBlockBytes: 10);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[4096];

                var act = () =>
                {
                    int framesRead;
                    while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 4096)) > 0)
                    {
                    }
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("sample_g711_mulaw_mono.wav", "sample_g711_mulaw_mono_expected.pcm", 1)]
        [InlineData("sample_g711_alaw_mono.wav", "sample_g711_alaw_mono_expected.pcm", 1)]
        [InlineData("sample_g711_mulaw_stereo.wav", "sample_g711_mulaw_stereo_expected.pcm", 2)]
        [InlineData("sample_g711_alaw_stereo.wav", "sample_g711_alaw_stereo_expected.pcm", 2)]
        public void Open_G711_Should_Decode_BitExact_Against_FfmpegGroundTruth(string fixtureFileName, string expectedFileName, int expectedChannels)
        {
            // Ground truth generated once by decoding a real ffmpeg-produced G.711 file via ffmpeg's
            // own pcm_mulaw/pcm_alaw decoder -- cross-checked separately (during development, not
            // re-asserted per test run) against macOS's own afconvert/CoreAudio decode of the same
            // mono files, which agreed bit-exact. Covers both companding laws and both channel counts.
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Wav/{expectedFileName}");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.Channels.Should().Be(expectedChannels);
            wavReader.SampleRate.Should().Be(8000);
            wavReader.BitsPerSample.Should().Be(16, "G.711 decodes to 16-bit PCM resolution regardless of its own 8-bit coded storage width");
            wavReader.TotalSamples.Should().Be(16000);

            var decoded = DecodeAll(wavReader);

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_MuLawWav_IsMuLaw_Should_Be_True_And_IsALaw_Should_Be_False()
        {
            using var wavReader = WavReader.Open(Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono.wav"));

            wavReader.IsMuLaw.Should().BeTrue();
            wavReader.IsALaw.Should().BeFalse();
            wavReader.IsImaAdpcm.Should().BeFalse();
            wavReader.IsFloatFormat.Should().BeFalse();
        }

        [Fact]
        public void Open_ALawWav_IsALaw_Should_Be_True_And_IsMuLaw_Should_Be_False()
        {
            using var wavReader = WavReader.Open(Path.GetFullPath("Codecs/Wav/sample_g711_alaw_mono.wav"));

            wavReader.IsALaw.Should().BeTrue();
            wavReader.IsMuLaw.Should().BeFalse();
        }

        [Fact]
        public void Open_G711_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            // G.711 has no block structure at all, but this still confirms the generic byte-width
            // override (1 byte/sample on disk despite a reported 16-bit decoded resolution) behaves
            // correctly across many small reads, not just one comfortably large one.
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_g711_mulaw_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37];
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Theory]
        [InlineData("sample_ms_adpcm_mono.wav", "sample_ms_adpcm_mono_expected.pcm", 1)]
        [InlineData("sample_ms_adpcm_stereo.wav", "sample_ms_adpcm_stereo_expected.pcm", 2)]
        public void Open_MsAdpcm_Should_Decode_BitExact_Against_FfmpegAndCoreAudio_GroundTruth(string fixtureFileName, string expectedFileName, int expectedChannels)
        {
            // Ground truth generated once by decoding a real ffmpeg-produced MS ADPCM file via
            // ffmpeg's own adpcm_ms decoder, cross-checked separately (during development, not
            // re-asserted per test run) against macOS's own afconvert/CoreAudio decode of the same
            // mono file, which agreed bit-exact. Both real files share blockAlign=1024, but
            // wSamplesPerBlock differs per the format's own channel-dependent formula: 2036 for mono,
            // 1012 for stereo (verified directly from each file's own fmt chunk bytes). Both happen
            // to make every block's own remainingSamplesPerChannel exactly even -- the "odd trailing
            // sample" branch is covered separately by MsAdpcmDecoderTest's own hand-crafted block,
            // not by a real fixture.
            var fixturePath = Path.GetFullPath($"Codecs/Wav/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Wav/{expectedFileName}");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.IsMsAdpcm.Should().BeTrue();
            wavReader.Channels.Should().Be(expectedChannels);
            wavReader.SampleRate.Should().Be(22050);
            wavReader.BitsPerSample.Should().Be(16, "MS ADPCM decodes to 16-bit PCM resolution regardless of its own 4-bit coded storage width");
            wavReader.TotalSamples.Should().Be(44100);

            var decoded = DecodeAll(wavReader);
            var expected = ReadGroundTruthPcm16(expectedPath);
            var expectedTrimmed = expected.Take(decoded.Count).ToArray();

            decoded.Should().Equal(expectedTrimmed);
        }

        [Fact]
        public void Open_MsAdpcm_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37]; // deliberately not a divisor of the real block's own samplesPerBlock
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            var expected = ReadGroundTruthPcm16(expectedPath).Take(decoded.Count).ToArray();

            decoded.Should().Equal(expected);
        }

        [Fact]
        public void Open_MsAdpcmWav_IsMsAdpcm_Should_Be_True_And_OtherFormatFlags_Should_Be_False()
        {
            using var wavReader = WavReader.Open(Path.GetFullPath("Codecs/Wav/sample_ms_adpcm_mono.wav"));

            wavReader.IsMsAdpcm.Should().BeTrue();
            wavReader.IsImaAdpcm.Should().BeFalse();
            wavReader.IsALaw.Should().BeFalse();
            wavReader.IsMuLaw.Should().BeFalse();
            wavReader.IsFloatFormat.Should().BeFalse();
        }

        [Fact]
        public void Open_MsAdpcm_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 3, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2036, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_FmtChunkTooShortForExtension_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateWithTruncatedFmtChunk(filePath);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_FmtChunkTooShortForCoefficientTable_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateWithTruncatedCoefficientTable(filePath);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_CbSizeTooShort_Should_Throw()
        {
            // Distinct from FmtChunkTooShortForExtension: here the chunk itself declares plenty of
            // physical room (22 bytes), but its own cbSize sub-field claims fewer than the 4 bytes
            // wSamplesPerBlock/wNumCoef need -- a declared-size-vs-declared-cbSize mismatch, not a
            // declared-size-vs-actual-room one.
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateWithCbSizeTooShort(filePath);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_ZeroCoefficients_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateWithZeroCoefficients(filePath);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_SamplesPerBlockTooSmall_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_BlockAlignTooSmallForHeader_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // channels=1 needs a 7-byte header alone; blockAlign=5 can't even hold that.
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 5, samplesPerBlock: 3, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_With_SamplesPerBlockExceedingBlockAlignCapacity_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                // channels=1, blockAlign=8 -> 7 header bytes + 1 data byte (2 nibbles) can hold at
                // most 2 (header) + 2 = 4 samples; declaring 5 claims more than the block can supply.
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 8, samplesPerBlock: 5, totalSamples: 1);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_WithoutFactChunk_Should_FallBack_To_BlockCountDerivedTotal()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2036, totalSamples: null, blockCount: 2);

                using var wavReader = WavReader.Open(filePath);

                wavReader.TotalSamples.Should().Be(2 * 2036);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_MsAdpcm_WithTruncatedFinalBlock_Should_Stop_Without_Throwing()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavMsAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, blockAlign: 1024, samplesPerBlock: 2036, totalSamples: 4072, blockCount: 2, truncateLastBlockBytes: 10);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[4096];

                var act = () =>
                {
                    int framesRead;
                    while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 4096)) > 0)
                    {
                    }
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_YamahaAdpcmMono_Should_Decode_BitExact_Against_FfmpegGroundTruth()
        {
            // Same rationale as the IMA/MS ADPCM cross-checks above -- a from-scratch implementation
            // of a well-standardized algorithm, bit-exact against a real ffmpeg-produced file decoded
            // by ffmpeg's own independent decoder.
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.Channels.Should().Be(1);
            wavReader.SampleRate.Should().Be(44100);
            wavReader.BitsPerSample.Should().Be(16, "Yamaha ADPCM decodes to 16-bit PCM resolution regardless of its own 4-bit coded storage width");
            wavReader.TotalSamples.Should().Be(88200, "the file's own 'fact' chunk is the authoritative total, trimming the data chunk's own trailing padding");

            var decoded = DecodeAll(wavReader);

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath).Take(decoded.Count));
        }

        [Fact]
        public void Open_YamahaAdpcmStereo_Should_Decode_BitExact_Against_FfmpegGroundTruth()
        {
            // Same rationale as the mono case, but this is the one real file that exercises the
            // low-nibble=channel-0/high-nibble=channel-1 per-frame interleaving specifically (two
            // different source tones, so a channel-swap or cross-contamination bug would be
            // immediately visible as wrong pitch/phase in one channel, not just a subtly-off sample).
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_stereo.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_stereo_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);
            wavReader.Channels.Should().Be(2);
            wavReader.TotalSamples.Should().Be(88200);

            var decoded = DecodeAll(wavReader);

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath).Take(decoded.Count));
        }

        [Fact]
        public void Open_YamahaAdpcm_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            // Yamaha ADPCM has no block structure at all, so unlike the IMA/MS ADPCM analogues of this
            // test, the thing actually being exercised here is the one-nibble-pending-across-calls
            // carry (YamahaAdpcmDecoder's own doc comment) at arbitrary, non-byte-aligned buffer
            // boundaries -- a buffer size that's odd relative to channel count forces a high nibble to
            // be carried from one ReadInterleavedSamples call into the next repeatedly.
            var fixturePath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono.wav");
            var expectedPath = Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono_expected.pcm");

            using var wavReader = WavReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37];
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            decoded.Should().Equal(ReadGroundTruthPcm16(expectedPath).Take(decoded.Count));
        }

        [Fact]
        public void Open_YamahaAdpcmWav_IsYamahaAdpcm_Should_Be_True_And_OtherFormatFlags_Should_Be_False()
        {
            using var wavReader = WavReader.Open(Path.GetFullPath("Codecs/Wav/sample_yamaha_adpcm_mono.wav"));

            wavReader.IsYamahaAdpcm.Should().BeTrue();
            wavReader.IsImaAdpcm.Should().BeFalse();
            wavReader.IsMsAdpcm.Should().BeFalse();
            wavReader.IsALaw.Should().BeFalse();
            wavReader.IsMuLaw.Should().BeFalse();
            wavReader.IsFloatFormat.Should().BeFalse();
        }

        [Fact]
        public void Open_YamahaAdpcm_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                WavYamahaAdpcmFileBuilder.CreateMinimal(filePath, channels: 3, sampleRate: 44100, totalSamples: 1, dataSize: 4);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_YamahaAdpcm_WithoutFactChunk_Should_FallBack_To_DataChunkDerivedTotal()
        {
            // The 'fact' chunk is the preferred, authoritative source for TotalSamples, but it's not
            // structurally required by the format tag itself -- a file missing it entirely should
            // still decode using the raw nibble-count-derived formula instead of failing outright.
            var filePath = Path.GetTempFileName();
            try
            {
                // mono: 100 bytes -> 200 nibbles -> 200 frames (channels=1).
                WavYamahaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, totalSamples: null, dataSize: 100);

                using var wavReader = WavReader.Open(filePath);

                wavReader.TotalSamples.Should().Be(200);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_YamahaAdpcm_WithTruncatedData_Should_Stop_Without_Throwing()
        {
            // A genuinely malformed input -- a 'fact' chunk total claiming far more frames than the
            // data chunk's own byte count can actually supply -- is one this project doesn't try to
            // recover partial data from: ReadInterleavedSamples should just stop cleanly (return 0
            // once exhausted) rather than throw or read out of bounds.
            var filePath = Path.GetTempFileName();
            try
            {
                // mono: 10 bytes -> only 20 real frames available, but 'fact' claims 1000.
                WavYamahaAdpcmFileBuilder.CreateMinimal(filePath, channels: 1, sampleRate: 44100, totalSamples: 1000, dataSize: 10);

                using var wavReader = WavReader.Open(filePath);
                var buffer = new int[4096];

                var act = () =>
                {
                    int framesRead;
                    while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 4096)) > 0)
                    {
                    }
                };

                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithUnsupportedFormatTag_Should_Throw()
        {
            // Format tag 20 (ITU G.723 ADPCM) is a real, legitimately different, still-unsupported
            // format -- confirms the validation that lets PCM/float/G.711/IMA-ADPCM/MS-ADPCM/Yamaha-
            // ADPCM through continues to reject everything else, now that MS ADPCM's own tag (2) has
            // been added to that allow-list (format tag 2 was this test's own example before MS ADPCM
            // landed).
            var filePath = Path.GetTempFileName();
            try
            {
                CreateWavWithFormatTag(filePath, formatTag: 20);

                var act = () => WavReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void CreateWavWithFormatTag(string filePath, ushort formatTag)
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            const int dataSize = 16;

            writer.Write("RIFF"u8);
            writer.Write((uint)(36 + dataSize));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write((uint)16);
            writer.Write(formatTag);
            writer.Write((ushort)1);
            writer.Write((uint)8000);
            writer.Write((uint)16000);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write((uint)dataSize);
            writer.Write(new byte[dataSize]);
        }

        private static List<int> DecodeAll(WavReader wavReader)
        {
            var decoded = new List<int>();
            var buffer = new int[4096 * wavReader.Channels];
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, 4096)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead * wavReader.Channels));
            }

            return decoded;
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
    }
}
