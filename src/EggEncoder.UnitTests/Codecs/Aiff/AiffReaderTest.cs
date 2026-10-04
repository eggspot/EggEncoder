using EggEncoder.Codecs.Aiff;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;
using System.Buffers.Binary;

namespace EggEncoder.UnitTests.Codecs.Aiff
{
    public class AiffReaderTest
    {
        [Fact]
        public void Open_16Bit_Stereo_Should_Read_Header_And_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, 0, short.MaxValue, short.MinValue, -1, 1 };
                AiffFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                aiffReader.Channels.Should().Be(2);
                aiffReader.SampleRate.Should().Be(44100);
                aiffReader.BitsPerSample.Should().Be(16);
                aiffReader.TotalSamples.Should().Be(3);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

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
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 48000, bitsPerSample: 24, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_8Bit_Mono_Should_Keep_Samples_Signed()
        {
            // The opposite of WavReader's 8-bit convention -- AIFF's 8-bit samples are signed already,
            // so no unsigned-to-signed rebasing happens on the way in.
            var filePath = Path.GetTempFileName();
            try
            {
                var signedSamples = new[] { 0, 127, -128, -64 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 8000, bitsPerSample: 8, signedSamples);

                using var aiffReader = AiffReader.Open(filePath);
                aiffReader.BitsPerSample.Should().Be(8);

                var buffer = new int[signedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: signedSamples.Length);

                framesRead.Should().Be(signedSamples.Length);
                buffer.Should().Equal(signedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_32Bit_Mono_Should_Read_FullRange_Samples()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var interleavedSamples = new[] { 0, int.MaxValue, int.MinValue, -12345678 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 32, interleavedSamples);

                using var aiffReader = AiffReader.Open(filePath);

                var buffer = new int[interleavedSamples.Length];
                var framesRead = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: interleavedSamples.Length);

                framesRead.Should().Be(interleavedSamples.Length);
                buffer.Should().Equal(interleavedSamples);
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
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3]);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[3];

                aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);
                var framesReadAfterEnd = aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3);

                framesReadAfterEnd.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void ReadInterleavedSamples_Called_Repeatedly_With_Varying_Sizes_Should_Not_Leak_Stale_Bytes()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2, 3, 4, 5, 6, 7, 8]);

                using var aiffReader = AiffReader.Open(filePath);

                var firstBuffer = new int[6];
                var firstFramesRead = aiffReader.ReadInterleavedSamples(firstBuffer, maxSamplesPerChannel: 6);

                var secondBuffer = new int[2];
                var secondFramesRead = aiffReader.ReadInterleavedSamples(secondBuffer, maxSamplesPerChannel: 2);

                firstFramesRead.Should().Be(6);
                firstBuffer.Should().Equal(1, 2, 3, 4, 5, 6);
                secondFramesRead.Should().Be(2);
                secondBuffer.Should().Equal(7, 8);
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
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 12, [0, 1]);

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_ZeroChannels_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, []);
                OverwriteChannelCount(filePath, 0);

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_FormHeader_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(filePath, "NOTF"u8.ToArray());

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing FORM header*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_NonAiffFormType_Should_Throw()
        {
            // "8SVX" (Amiga 8SVX) is a real, genuinely-still-unsupported IFF form type -- "AIFC" was
            // this test's own example before AIFC support landed (see Open_AifcFormType_WithNoCommChunk_Should_Throw
            // below for what a bare FORM/AIFC header now does instead: proceeds past this check and
            // fails later for a different, AIFC-specific reason).
            var filePath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("FORM"u8);
                    writer.Write(0u);
                    writer.Write("8SVX"u8);
                }

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing AIFF/AIFC form type*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_AifcFormType_WithNoCommChunk_Should_Throw()
        {
            // Confirms FORM/AIFC is genuinely accepted now, not just no-longer-rejected-at-the-form-type
            // check for an unrelated reason -- a bare FORM/AIFC header with nothing else fails for the
            // same "missing a 'COMM' chunk" reason a bare FORM/AIFF header already does, not for its
            // form type.
            var filePath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("FORM"u8);
                    writer.Write(0u);
                    writer.Write("AIFC"u8);
                }

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing a 'COMM' chunk*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_CommChunk_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("FORM"u8);
                    writer.Write(0u);
                    writer.Write("AIFF"u8);
                }

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing a 'COMM' chunk*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Missing_SsndChunk_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16, [1, 2]);
                TruncateAfterCommChunk(filePath);

                var act = () => AiffReader.Open(filePath).Dispose();
                act.Should().ThrowExactly<InvalidDataException>().WithMessage("*missing an 'SSND' chunk*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_With_OddSizedChunk_Should_Skip_The_Pad_Byte()
        {
            // 1 channel, 8-bit, 3 frames = 3 (odd) data bytes -- AiffFileBuilder writes the IFF pad byte.
            // If AiffReader didn't account for it, the chunk-walk would misalign on any chunk after SSND;
            // here SSND is last, so this only self-validates by reading the three samples back correctly.
            var filePath = Path.GetTempFileName();
            try
            {
                var samples = new[] { 1, -1, 2 };
                AiffFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 8, samples);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[3];
                aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 3).Should().Be(3);
                buffer.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("fixture_none_mono.aifc", "fixture_none_mono_expected.pcm")]
        [InlineData("fixture_sowt_mono.aifc", "fixture_sowt_mono_expected.pcm")]
        public void Open_Aifc_IntegerCompressionType_Should_Decode_BitExact_Against_RealFixture(string fixtureFileName, string expectedFileName)
        {
            // Ground truth generated once by parsing each real file's own raw COMM/SSND bytes directly
            // (fixture_none_mono.aifc via macOS's own afconvert, which writes 'twos' for plain big-endian
            // PCM inside an AIFC container; fixture_sowt_mono.aifc via ffmpeg's pcm_s16le-into-aiff muxer,
            // which writes 'sowt') -- both fixtures encode the same underlying 440Hz sine, confirmed by
            // their independently-derived ground truths agreeing sample-for-sample despite the different
            // on-disk byte order, which is exactly what this test is verifying AiffReader gets right.
            var fixturePath = Path.GetFullPath($"Codecs/Aiff/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Aiff/{expectedFileName}");

            using var aiffReader = AiffReader.Open(fixturePath);
            aiffReader.Channels.Should().Be(1);
            aiffReader.SampleRate.Should().Be(44100);
            aiffReader.BitsPerSample.Should().Be(16);
            aiffReader.TotalSamples.Should().Be(4410);
            aiffReader.IsFloatFormat.Should().BeFalse();
            aiffReader.IsALaw.Should().BeFalse();
            aiffReader.IsMuLaw.Should().BeFalse();

            var buffer = new int[aiffReader.TotalSamples];
            aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples).Should().Be((int)aiffReader.TotalSamples);

            buffer.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_Aifc_None_IsLittleEndian_Should_Be_False()
        {
            using var aiffReader = AiffReader.Open(Path.GetFullPath("Codecs/Aiff/fixture_none_mono.aifc"));
            aiffReader.IsLittleEndian.Should().BeFalse();
        }

        [Fact]
        public void Open_Aifc_Sowt_IsLittleEndian_Should_Be_True()
        {
            using var aiffReader = AiffReader.Open(Path.GetFullPath("Codecs/Aiff/fixture_sowt_mono.aifc"));
            aiffReader.IsLittleEndian.Should().BeTrue();
        }

        [Theory]
        [InlineData("fixture_fl32_mono.aifc", "fixture_fl32_mono_expected.pcm")]
        [InlineData("fixture_fl64_mono.aifc", "fixture_fl64_mono_expected.pcm")]
        public void Open_Aifc_FloatCompressionType_Should_Decode_BitExact_Against_RealFixture(string fixtureFileName, string expectedFileName)
        {
            // Ground truth generated once by parsing each real ffmpeg-produced file's own raw big-endian
            // float/double SSND bytes directly and applying this codebase's own int32-native-range scale
            // (clamp to -1.0..1.0, multiply by int.MaxValue) -- the same scale WavReader's own float WAV
            // decode already uses, NOT ffmpeg's own s32le export convention (which scales by 2^31 and
            // rounds differently, confirmed to differ by exactly 1 LSB in places during this fixture's
            // own preparation -- expected and irrelevant, since this project's own convention, not
            // ffmpeg's internal one, is what AiffReader must match). fl32 and fl64 encode the identical
            // underlying floats (confirmed: both fixtures' ground truths are byte-for-byte identical),
            // so both report BitsPerSample 32 and decode into the exact same int32 values -- only the
            // on-disk coded width (4 vs 8 bytes) differs.
            var fixturePath = Path.GetFullPath($"Codecs/Aiff/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Aiff/{expectedFileName}");

            using var aiffReader = AiffReader.Open(fixturePath);
            aiffReader.Channels.Should().Be(1);
            aiffReader.SampleRate.Should().Be(44100);
            aiffReader.BitsPerSample.Should().Be(32);
            aiffReader.TotalSamples.Should().Be(4410);
            aiffReader.IsFloatFormat.Should().BeTrue();

            var buffer = new int[aiffReader.TotalSamples];
            aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples).Should().Be((int)aiffReader.TotalSamples);

            buffer.Should().Equal(ReadGroundTruthPcm32(expectedPath));
        }

        [Fact]
        public void Open_Aifc_Fl32_IsFloat32_True_IsFloat64_False()
        {
            using var aiffReader = AiffReader.Open(Path.GetFullPath("Codecs/Aiff/fixture_fl32_mono.aifc"));
            aiffReader.IsFloat32.Should().BeTrue();
            aiffReader.IsFloat64.Should().BeFalse();
        }

        [Fact]
        public void Open_Aifc_Fl64_IsFloat64_True_IsFloat32_False()
        {
            using var aiffReader = AiffReader.Open(Path.GetFullPath("Codecs/Aiff/fixture_fl64_mono.aifc"));
            aiffReader.IsFloat64.Should().BeTrue();
            aiffReader.IsFloat32.Should().BeFalse();
        }

        [Fact]
        public void Open_Aifc_Fl32_WithNaNOrInfinity_Should_Clamp_Not_Throw()
        {
            // Float32ToInt32 delegates to FloatSampleConverter.ClampToNativeInt32(float), which has its
            // own dedicated NaN/Infinity test coverage elsewhere -- this just confirms AiffReader's fl32
            // decode path genuinely reaches it (not, say, silently skipping the clamp) for real
            // adversarial/synthesized bytes, not just well-behaved sine-wave fixture data.
            var filePath = Path.GetTempFileName();
            try
            {
                var bytes = new byte[16];
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(0, 4), float.NaN);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4, 4), float.PositiveInfinity);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(8, 4), float.NegativeInfinity);
                BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(12, 4), 0.5f);
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize: 32, "fl32", bytes);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[4];
                aiffReader.ReadInterleavedSamples(buffer, 4).Should().Be(4);

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
        public void Open_Aifc_Fl64_WithNaNOrInfinity_Should_Clamp_Not_Throw()
        {
            // Unlike fl32, Float64ToInt32 is AiffReader's own method -- not shared with any
            // already-tested code elsewhere -- so its NaN/Infinity handling needs its own direct
            // coverage, not just an inherited guarantee from FloatSampleConverter's test suite.
            var filePath = Path.GetTempFileName();
            try
            {
                var bytes = new byte[32];
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(0, 8), double.NaN);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(8, 8), double.PositiveInfinity);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(16, 8), double.NegativeInfinity);
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(24, 8), 0.5);
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize: 64, "fl64", bytes);

                using var aiffReader = AiffReader.Open(filePath);
                var buffer = new int[4];
                aiffReader.ReadInterleavedSamples(buffer, 4).Should().Be(4);

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

        [Theory]
        [InlineData("fixture_alaw_mono.aifc", "fixture_alaw_mono_expected.pcm", true, false)]
        [InlineData("fixture_ulaw_mono.aifc", "fixture_ulaw_mono_expected.pcm", false, true)]
        public void Open_Aifc_G711CompressionType_Should_Decode_BitExact_Against_RealFixture(string fixtureFileName, string expectedFileName, bool expectALaw, bool expectMuLaw)
        {
            // Ground truth cross-checked bit-exact against ffmpeg's own decode of these same two real
            // files during this fixture's own preparation (not re-asserted per test run) -- confirming
            // Wav.G711Codec's already-verified algorithm produces the identical result when reused here,
            // through AIFC's own container rather than WAV's.
            var fixturePath = Path.GetFullPath($"Codecs/Aiff/{fixtureFileName}");
            var expectedPath = Path.GetFullPath($"Codecs/Aiff/{expectedFileName}");

            using var aiffReader = AiffReader.Open(fixturePath);
            aiffReader.Channels.Should().Be(1);
            aiffReader.SampleRate.Should().Be(44100);
            aiffReader.BitsPerSample.Should().Be(16, "G.711 decodes to 16-bit PCM resolution regardless of its own 8-bit coded storage width");
            aiffReader.TotalSamples.Should().Be(4410);
            aiffReader.IsALaw.Should().Be(expectALaw);
            aiffReader.IsMuLaw.Should().Be(expectMuLaw);

            var buffer = new int[aiffReader.TotalSamples];
            aiffReader.ReadInterleavedSamples(buffer, (int)aiffReader.TotalSamples).Should().Be((int)aiffReader.TotalSamples);

            buffer.Should().Equal(ReadGroundTruthPcm16(expectedPath));
        }

        [Fact]
        public void Open_Aifc_CalledWithSmallBuffers_Should_StillProduceTheSameBitExactOutput()
        {
            var fixturePath = Path.GetFullPath("Codecs/Aiff/fixture_fl32_mono.aifc");
            var expectedPath = Path.GetFullPath("Codecs/Aiff/fixture_fl32_mono_expected.pcm");

            using var aiffReader = AiffReader.Open(fixturePath);

            var decoded = new List<int>();
            var buffer = new int[37]; // deliberately not a divisor of the fixture's own 4410 total samples
            int framesRead;
            while ((framesRead = aiffReader.ReadInterleavedSamples(buffer, 37)) > 0)
            {
                decoded.AddRange(buffer.Take(framesRead));
            }

            decoded.Should().Equal(ReadGroundTruthPcm32(expectedPath));
        }

        [Fact]
        public void Open_Aifc_Stereo_Sowt_Should_Interleave_Channels_Correctly()
        {
            // Independent-of-AiffWriter stereo coverage: two little-endian 16-bit frames, channel 0 and
            // channel 1 each with their own distinct, easily-recognizable value, so a channel-order or
            // byte-order mistake in DecodeLittleEndianInteger's stereo path would show up unmistakably.
            var filePath = Path.GetTempFileName();
            try
            {
                byte[] sampleBytes =
                [
                    0xD0, 0x07, // ch0 frame0 = 2000 (LE)
                    0x30, 0xF8, // ch1 frame0 = -2000 (LE, two's complement)
                    0xE8, 0x03, // ch0 frame1 = 1000 (LE)
                    0x18, 0xFC // ch1 frame1 = -1000 (LE)
                ];
                AifcFileBuilder.Create(filePath, channels: 2, sampleRate: 44100, sampleSize: 16, "sowt", sampleBytes);

                using var aiffReader = AiffReader.Open(filePath);
                aiffReader.Channels.Should().Be(2);
                aiffReader.IsLittleEndian.Should().BeTrue();

                var buffer = new int[4];
                aiffReader.ReadInterleavedSamples(buffer, maxSamplesPerChannel: 2).Should().Be(2);

                buffer.Should().Equal(2000, -2000, 1000, -1000);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Aifc_WithUnknownCompressionType_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize: 16, "bogu", [0, 0]);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage("*unsupported AIFC compressionType*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Aifc_WithIma4CompressionType_Should_Throw_WithDistinctMessage()
        {
            // ima4 (QuickTime IMA4 ADPCM) is a real, commonly-produced AIFC compressionType -- confirmed
            // via a real ffmpeg-produced fixture during this feature's own research -- but is deliberately
            // not decoded here (see AiffReader's own doc comment for why). It must fail with its own
            // distinct message naming it, not the generic "unsupported compressionType" one, so a caller
            // can tell "known but unsupported" apart from "genuinely unrecognized".
            var filePath = Path.GetTempFileName();
            try
            {
                // sampleSize: 4 matches ima4's own real coded width (confirmed via ffprobe against a real
                // ffmpeg-produced fixture), which CreateWithExplicitFrameCount is needed for -- the plain
                // Create overload derives totalFrames via sampleSize / 8, which truncates to 0 (and would
                // divide by zero) for any coded width under 8 bits; moot here anyway since Open() throws
                // before totalFrames is ever read.
                AifcFileBuilder.CreateWithExplicitFrameCount(filePath, channels: 1, sampleRate: 44100, sampleSize: 4, "ima4", [0, 0], totalFrames: 1);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage("*ima4*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Aifc_WithCommChunkTooShortForCompressionType_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AifcFileBuilder.CreateWithCommChunkTooShortForCompressionType(filePath);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(24)]
        public void Open_Aifc_Fl32_WithWrongSampleSize_Should_Throw(int sampleSize)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var byteWidth = sampleSize / 8;
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize, "fl32", new byte[byteWidth * 2]);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage("*fl32*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Aifc_Fl64_WithWrongSampleSize_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize: 32, "fl64", new byte[8]);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage("*fl64*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Theory]
        [InlineData("alaw")]
        [InlineData("ulaw")]
        public void Open_Aifc_G711_WithWrongSampleSize_Should_Throw(string compressionType)
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AifcFileBuilder.Create(filePath, channels: 1, sampleRate: 44100, sampleSize: 16, compressionType, [0, 0, 0, 0]);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>().WithMessage($"*{compressionType}*");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Open_Aifc_Sowt_WithUnsupportedSampleSize_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                AifcFileBuilder.CreateWithExplicitFrameCount(filePath, channels: 1, sampleRate: 44100, sampleSize: 12, "sowt", [0, 0], totalFrames: 1);

                var act = () => AiffReader.Open(filePath).Dispose();

                act.Should().ThrowExactly<NotSupportedException>();
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

        private static void OverwriteChannelCount(string filePath, short channels)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Seek(12 + 8, SeekOrigin.Begin); // FORM header (12) + COMM id/size (8), channels field follows immediately
            var bytes = new byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(bytes, channels);
            stream.Write(bytes);
        }

        private static void TruncateAfterCommChunk(string filePath)
        {
            const int formHeaderSize = 12;
            const int commChunkTotalSize = 8 + 18;

            var bytes = File.ReadAllBytes(filePath);
            File.WriteAllBytes(filePath, bytes[..(formHeaderSize + commChunkTotalSize)]);
        }
    }
}
