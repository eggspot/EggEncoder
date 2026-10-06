using EggEncoder.Codecs.Au;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using System.Buffers.Binary;

namespace EggEncoder.UnitTests.Codecs.Au
{
    public class AuReaderTest
    {
        [Theory]
        [InlineData("fixture_s16be_mono.au", "fixture_s16be_mono_expected.pcm", 16, false, false, false, false)]
        [InlineData("fixture_s8_mono.au", "fixture_s8_mono_expected.pcm", 8, false, false, false, false)]
        [InlineData("fixture_s24be_mono.au", "fixture_s24be_mono_expected.pcm", 24, false, false, false, false)]
        [InlineData("fixture_s32be_mono.au", "fixture_s32be_mono_expected.pcm", 32, false, false, false, false)]
        [InlineData("fixture_f32be_mono.au", "fixture_f32be_mono_expected.pcm", 32, true, false, false, false)]
        [InlineData("fixture_f64be_mono.au", "fixture_f64be_mono_expected.pcm", 32, false, true, false, false)]
        [InlineData("fixture_mulaw_mono.au", "fixture_mulaw_mono_expected.pcm", 16, false, false, false, true)]
        [InlineData("fixture_alaw_mono.au", "fixture_alaw_mono_expected.pcm", 16, false, false, true, false)]
        public void Open_Should_Decode_BitExact_Against_RealFixture(string fixtureFileName, string expectedFileName, int expectedBitsPerSample, bool expectFloat32, bool expectFloat64, bool expectALaw, bool expectMuLaw)
        {
            // Ground truth generated once by parsing each real ffmpeg-produced file's own raw header/
            // data bytes directly (int/float cases: applying this codebase's own int32-native-range
            // scale; G.711 cases: cross-checked bit-exact against ffmpeg's own decode of the same two
            // files during this fixture's own preparation, not re-asserted per test run).
            var fixturePath = Path.GetFullPath($"Codecs/Au/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Au/{expectedFileName}");

            using var auReader = AuReader.Open(fixturePath);
            auReader.Channels.Should().Be(1);
            auReader.SampleRate.Should().Be(44100);
            auReader.BitsPerSample.Should().Be(expectedBitsPerSample);
            auReader.TotalSamples.Should().Be(4410);
            auReader.IsFloat32.Should().Be(expectFloat32);
            auReader.IsFloat64.Should().Be(expectFloat64);
            auReader.IsALaw.Should().Be(expectALaw);
            auReader.IsMuLaw.Should().Be(expectMuLaw);
            auReader.IsFloatFormat.Should().Be(expectFloat32 || expectFloat64);

            var buffer = new int[auReader.TotalSamples];
            auReader.ReadInterleavedSamples(buffer, (int)auReader.TotalSamples).Should().Be((int)auReader.TotalSamples);

            var expected = expectedBitsPerSample switch
            {
                8 or 16 => ReadGroundTruthPcm16(expectedPath),
                _ => ReadGroundTruthPcm32(expectedPath)
            };
            buffer.Should().Equal(expected);
        }

        [Fact]
        public void Open_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            var fixturePath = Path.GetFullPath("Codecs/Au/fixture_s24be_mono.au");
            var expectedPath = Path.GetFullPath("Codecs/Au/fixture_s24be_mono_expected.pcm");

            using var auReader = AuReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37]; // deliberately not a divisor of the fixture's own 4410 total samples
            int framesRead;
            while ((framesRead = auReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            decoded.Should().Equal(ReadGroundTruthPcm32(expectedPath));
        }

        [Fact]
        public void Open_Stereo_Should_Interleave_Channels_Correctly()
        {
            // Independent-of-AuWriter stereo coverage: all real fixtures above are mono, so this proves
            // AU's own (trivial, non-interleaved-at-the-byte-level) frame layout is handled correctly
            // for channels > 1 too, via a hand-built file.
            var filePath = Path.GetTempFileName();
            try
            {
                byte[] sampleBytes =
                [
                    0x07, 0xD0, 0xF8, 0x30, // frame0: ch0=2000, ch1=-2000 (big-endian)
                    0x03, 0xE8, 0xFC, 0x18 // frame1: ch0=1000, ch1=-1000
                ];
                AuFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, encoding: 3, sampleBytes);

                using var auReader = AuReader.Open(filePath);
                auReader.Channels.Should().Be(2);

                var buffer = new int[4];
                auReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 2).Should().Be(2);

                buffer.Should().Equal(2000, -2000, 1000, -1000);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithAnnotationAndUnknownDataSize_Should_ReadUntilEndOfFile()
        {
            // AU's own "unknown size" sentinel (0xFFFFFFFF) is a real value a streaming encoder can
            // write -- AuWriter never writes it (see its own doc comment), but a real-world file using
            // it must still decode correctly, deriving the true byte count from the file's own length
            // rather than trusting a declared dataSize.
            var filePath = Path.GetTempFileName();
            try
            {
                byte[] samples = [0x00, 0x01, 0x00, 0x02, 0x00, 0x03]; // 3 16-bit big-endian samples
                AuFileBuilder.CreateWithAnnotationAndUnknownDataSize(filePath, channels: 1, sampleRate: 44100, encoding: 3, samples, annotationLength: 8);

                using var auReader = AuReader.Open(filePath);
                auReader.TotalSamples.Should().Be(3);

                var buffer = new int[3];
                auReader.ReadInterleavedSamples(buffer, 3).Should().Be(3);
                buffer.Should().Equal(1, 2, 3);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithUnsupportedEncoding_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AuFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, encoding: 23, [0, 0]);

                var act = () => AuReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage("*unsupported AU encoding*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithTooSmallHeaderSize_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AuFileBuilder.CreateWithTooSmallHeaderSize(filePath);

                var act = () => AuReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithMissingMagic_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(filePath, "NOTF"u8.ToArray());

                var act = () => AuReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing '.snd' magic*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_WithZeroChannels_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AuFileBuilder.Create(filePath, channels: 0, sampleRate: 44100, encoding: 3, [0, 0]);

                var act = () => AuReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Fl32_WithNaNOrInfinity_Should_Clamp_Not_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var bytes = new byte[16];
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(0, 4), float.NaN);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4, 4), float.PositiveInfinity);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(8, 4), float.NegativeInfinity);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(12, 4), 0.5f);
                AuFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, encoding: 6, bytes);

                using var auReader = AuReader.Open(filePath);
                var buffer = new int[4];
                auReader.ReadInterleavedSamples(buffer, 4).Should().Be(4);

                buffer[0].Should().Be(0, "NaN maps to silence");
                buffer[1].Should().Be(int.MaxValue, "+Infinity clamps to the top of the native range");
                buffer[2].Should().Be(-int.MaxValue, "-Infinity clamps to the bottom of the native range");
                buffer[3].Should().Be((int)(0.5 * int.MaxValue));
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Fl64_WithNaNOrInfinity_Should_Clamp_Not_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var bytes = new byte[32];
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(0, 8), double.NaN);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(8, 8), double.PositiveInfinity);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(16, 8), double.NegativeInfinity);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(24, 8), 0.5);
                AuFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, encoding: 7, bytes);

                using var auReader = AuReader.Open(filePath);
                var buffer = new int[4];
                auReader.ReadInterleavedSamples(buffer, 4).Should().Be(4);

                buffer[0].Should().Be(0, "NaN maps to silence");
                buffer[1].Should().Be(int.MaxValue, "+Infinity clamps to the top of the native range");
                buffer[2].Should().Be(-int.MaxValue, "-Infinity clamps to the bottom of the native range");
                buffer[3].Should().Be((int)(0.5 * int.MaxValue));
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

        private static int[] ReadGroundTruthPcm32(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new int[bytes.Length / 4];
            for (var i = 0; i < samples.Length; i++)
            {
                var offset = i * 4;
                samples[i] = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);
            }

            return samples;
        }
    }
}
