using EggEncoder.Codecs;
using EggEncoder.Codecs.Aiff;
using FluentAssertions;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.UnitTests.Codecs.Aiff
{
    public class AiffWriterTest
    {
        [Fact]
        public void WriteInterleavedSamples_8Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 127, -128, -64 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                reader.Channels.Should().Be(1);
                reader.SampleRate.Should().Be(8000);
                reader.BitsPerSample.Should().Be(8);
                reader.TotalSamples.Should().Be(samples.Length);

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
        public void WriteInterleavedSamples_16Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, short.MaxValue, short.MinValue, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length / 2))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_24Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 8388607, -8388608, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
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
        public void WriteInterleavedSamples_32Bit_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
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
        public void WriteInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var firstBlock = new[] { 1, 2, 3, 4, 5, 6 };
                var secondBlock = new[] { 7, 8 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: firstBlock.Length + secondBlock.Length))
                {
                    writer.WriteInterleavedSamples(firstBlock, firstBlock.Length);
                    writer.WriteInterleavedSamples(secondBlock, secondBlock.Length);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[firstBlock.Length + secondBlock.Length];
                reader.ReadInterleavedSamples(buffer, buffer.Length);

                buffer.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_WithZeroOrNegativeFrameCount_Should_Write_Nothing()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 2))
                {
                    writer.WriteInterleavedSamples([1, 2], frameCount: 0);
                    writer.WriteInterleavedSamples([1, 2], frameCount: -1);
                    writer.WriteInterleavedSamples([1, 2], frameCount: 2);
                }

                using var reader = AiffReader.Open(filePath);
                reader.TotalSamples.Should().Be(2);

                var buffer = new int[2];
                reader.ReadInterleavedSamples(buffer, 2);
                buffer.Should().Equal(1, 2);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_OddTotalDataSize_Should_Write_A_Pad_Byte_Without_Corrupting_The_File()
        {
            // 1 channel, 8-bit, 3 frames = 3 (odd) bytes of sample data -- exercises AiffWriter's IFF
            // pad-byte handling on Dispose. If the pad byte (or the FORM/SSND size fields that account
            // for it) were wrong, this would either throw on Open or read back the wrong sample count.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1, 2 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                var fileBytes = File.ReadAllBytes(filePath);
                (fileBytes.Length % 2).Should().Be(0, "every IFF chunk, and so the whole file, must end on an even byte boundary");

                using var reader = AiffReader.Open(filePath);
                reader.TotalSamples.Should().Be(3);

                var buffer = new int[3];
                reader.ReadInterleavedSamples(buffer, 3).Should().Be(3);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_EvenTotalDataSize_Should_Not_Write_A_Pad_Byte()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                // FORM header (12) + COMM chunk (8+18=26) + SSND chunk header (8) + offset/blockSize (8) + 2 data bytes, no pad.
                var expectedFileLength = 12 + 26 + 8 + 8 + 2;
                new FileInfo(filePath).Length.Should().Be(expectedFileLength);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(12)]
        [InlineData(33)]
        [InlineData(64)]
        public void Create_With_UnsupportedBitsPerSample_Should_Throw(int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithAiffSampleFormatInteger_Should_Write_ByteForByte_Identical_To_FiveParameterOverload()
        {
            // Confirms the new six-parameter overload's Integer case is truly a no-op passthrough to the
            // original behavior -- not just "decodes the same", but byte-for-byte identical on disk.
            var fivePath = Path.GetTempFileName();
            var sixPath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, short.MaxValue, short.MinValue, -1 };

                using (var writer = AiffWriter.Create(fivePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using (var writer = AiffWriter.Create(sixPath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, AiffSampleFormat.Integer))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                File.ReadAllBytes(sixPath).Should().Equal(File.ReadAllBytes(fivePath));
            }
            finally
            {
                File.Delete(fivePath);
                File.Delete(sixPath);
            }
        }

        [Fact]
        public void Create_WithLittleEndianInteger_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, short.MaxValue, short.MinValue, -1 };

                using (var writer = AiffWriter.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length / 2, AiffSampleFormat.LittleEndianInteger))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                }

                using var reader = AiffReader.Open(filePath);
                reader.IsLittleEndian.Should().BeTrue();
                reader.BitsPerSample.Should().Be(16);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length / 2);

                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithFloat32_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -1073741824 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, AiffSampleFormat.Float32))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                reader.IsFloat32.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // int.MinValue has no positive counterpart at this scale (a known, accepted asymmetry of
                // the int.MaxValue-based scale this codebase uses throughout -- the same one
                // FloatSampleConverter's own doc comment describes), so it round-trips to int.MinValue + 1
                // rather than itself. -1073741824 narrows to exactly -0.5f (float32 represents that
                // fraction exactly), then -0.5 * int.MaxValue = -1073741823.5, which (int) truncates
                // toward zero to -1073741823 -- a genuine, expected float32-narrowing precision loss
                // (confirmed independently in Python before writing this assertion), not a bug; see
                // Create_WithFloat64_Should_Preserve_More_Precision_Than_Float32 for the same value
                // round-tripping exactly once that narrowing step is removed.
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(int.MinValue + 1);
                buffer[3].Should().Be(-1073741823);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithFloat64_Should_Round_Trip_Through_AiffReader()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, int.MaxValue, int.MinValue, -1073741824 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: samples.Length, AiffSampleFormat.Float64))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                reader.IsFloat64.Should().BeTrue();
                reader.BitsPerSample.Should().Be(32);

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // Same int.MinValue asymmetry as Float32 (see that test's own comment) -- Float64's full
                // double precision doesn't change that it's a deliberate clamp-to-range decision, not a
                // rounding artifact. Unlike Float32, -1073741824 (the 4th sample) *does* round-trip
                // exactly here: no float32-narrowing step to lose precision at.
                buffer[0].Should().Be(0);
                buffer[1].Should().Be(int.MaxValue);
                buffer[2].Should().Be(int.MinValue + 1);
                buffer[3].Should().Be(-1073741824);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithFloat64_Should_Preserve_More_Precision_Than_Float32()
        {
            // A value that doesn't round-trip exactly through float32's 24-bit mantissa should still
            // round-trip exactly through float64's 53-bit one -- proving Float64ToInt32/Int32ToFloat64
            // genuinely avoid the float-narrowing step, not just route through the same float32 math
            // with a wider on-disk container.
            var sample = 123456789;

            var narrowed = (float)(sample / (double)int.MaxValue);
            var roundTrippedThroughFloat32 = (int)(Math.Clamp((double)narrowed, -1.0, 1.0) * int.MaxValue);
            roundTrippedThroughFloat32.Should().NotBe(sample, "the test's own premise requires float32 to actually lose precision for this value");

            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, totalFrames: 1, AiffSampleFormat.Float64))
                {
                    writer.WriteInterleavedSamples([sample], 1);
                }

                using var reader = AiffReader.Open(filePath);
                var buffer = new int[1];
                reader.ReadInterleavedSamples(buffer, 1);

                buffer[0].Should().Be(sample);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(AiffSampleFormat.ALaw)]
        [InlineData(AiffSampleFormat.MuLaw)]
        public void Create_WithG711Format_Should_Round_Trip_Through_AiffReader(AiffSampleFormat sampleFormat)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 0, 1000, -1000, 8000, -8000 };

                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: samples.Length, sampleFormat))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length);
                }

                using var reader = AiffReader.Open(filePath);
                reader.BitsPerSample.Should().Be(16);
                (sampleFormat == AiffSampleFormat.ALaw ? reader.IsALaw : reader.IsMuLaw).Should().BeTrue();

                var buffer = new int[samples.Length];
                reader.ReadInterleavedSamples(buffer, samples.Length);

                // G.711 is lossy companding, not exact PCM -- compare against the real codec's own
                // decode-of-its-own-encode rather than the original samples, the same way WAV's own
                // G.711 round-trip tests do.
                var expected = samples.Select(s => sampleFormat == AiffSampleFormat.ALaw
                    ? EggEncoder.Codecs.Wav.G711Codec.DecodeALaw(EggEncoder.Codecs.Wav.G711Codec.EncodeALaw(s))
                    : EggEncoder.Codecs.Wav.G711Codec.DecodeMuLaw(EggEncoder.Codecs.Wav.G711Codec.EncodeMuLaw(s))).ToArray();
                buffer.Should().Equal(expected);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_WithAifcFormat_Should_Write_Expected_FverAndExtendedCommBytes()
        {
            // Verifies the exact container shape against the real, ffmpeg/afconvert-confirmed layout
            // this feature's own research established -- not just "AiffReader can read it back", which
            // wouldn't catch AiffWriter and AiffReader sharing the same mirrored bug.
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, totalFrames: 1, AiffSampleFormat.LittleEndianInteger))
                {
                    writer.WriteInterleavedSamples([42], 1);
                }

                var bytes = File.ReadAllBytes(filePath);

                Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("FORM");
                Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("AIFC");
                Encoding.ASCII.GetString(bytes, 12, 4).Should().Be("FVER");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(4);
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(0xA2805140);

                Encoding.ASCII.GetString(bytes, 24, 4).Should().Be("COMM");
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28, 4)).Should().Be(24, "18 base + 4 compressionType + 2 (empty compressionName: length byte + pad byte)");
                // COMM payload starts at 32: channels(2) + numSampleFrames(4) + sampleSize(2) + sampleRate(10) = 18 bytes, so compressionType starts at 32+18=50.
                Encoding.ASCII.GetString(bytes, 50, 4).Should().Be("sowt");
                bytes[54].Should().Be(0, "compressionName length (empty)");
                bytes[55].Should().Be(0, "compressionName pad byte");

                Encoding.ASCII.GetString(bytes, 56, 4).Should().Be("SSND");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(AiffSampleFormat.Float32, 16)]
        [InlineData(AiffSampleFormat.Float64, 16)]
        [InlineData(AiffSampleFormat.ALaw, 8)]
        [InlineData(AiffSampleFormat.MuLaw, 32)]
        public void Create_WithWrongBitsPerSampleForFormat_Should_Throw(AiffSampleFormat sampleFormat, int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1, sampleFormat);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(12)]
        [InlineData(33)]
        public void Create_WithLittleEndianInteger_UnsupportedBitsPerSample_Should_Throw(int bitsPerSample)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AiffWriter.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample, totalFrames: 1, AiffSampleFormat.LittleEndianInteger);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Create_With_HighSampleRate_Should_Round_Trip_Via_IeeeExtendedFloat()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var writer = AiffWriter.Create(filePath, channels: 1, sampleRate: 192000, bitsPerSample: 16, totalFrames: 1))
                {
                    writer.WriteInterleavedSamples([42], 1);
                }

                using var reader = AiffReader.Open(filePath);
                reader.SampleRate.Should().Be(192000);
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
