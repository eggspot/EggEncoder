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

        [Fact]
        public void Open_WithUnsupportedFormatTag_Should_Throw()
        {
            // Format tag 2 (MS ADPCM) is a real, legitimately different, still-unsupported format --
            // confirms the validation that lets PCM/float/G.711/IMA-ADPCM through continues to reject
            // everything else, now that G.711's own tags (6/7) have been added to that allow-list.
            var filePath = Path.GetTempFileName();
            try
            {
                CreateWavWithFormatTag(filePath, formatTag: 2);

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
