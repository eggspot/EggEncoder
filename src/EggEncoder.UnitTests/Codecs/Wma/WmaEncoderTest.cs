using EggEncoder.Codecs.Wma;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wma
{
    public class WmaEncoderTest
    {
        [Fact]
        public void Encode_ThenDecode_Mono_Should_ReconstructToneSignal_UpToScale()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var originalSamples = GenerateInterleavedTone(sampleRate, seconds: 1, channels);
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_roundtrip_mono_{Guid.NewGuid():N}.wma");

            try
            {
                using (var session = WmaEncoderSession.OpenSession(outputPath, channels, sampleRate))
                {
                    session.WriteInterleavedSamples(originalSamples, originalSamples.Length / channels);
                    session.Finish();
                }

                AssertRoundTripSnr(outputPath, originalSamples, channels, sampleRate, minimumSnrDb: 10);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void Encode_ThenDecode_Stereo_Should_ReconstructToneSignal_UpToScale()
        {
            const int sampleRate = 44100;
            const int channels = 2;

            var originalSamples = GenerateInterleavedTone(sampleRate, seconds: 1, channels);
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_roundtrip_stereo_{Guid.NewGuid():N}.wma");

            try
            {
                using (var session = WmaEncoderSession.OpenSession(outputPath, channels, sampleRate))
                {
                    session.WriteInterleavedSamples(originalSamples, originalSamples.Length / channels);
                    session.Finish();
                }

                AssertRoundTripSnr(outputPath, originalSamples, channels, sampleRate, minimumSnrDb: 10);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void Encode_WholeBufferApi_Should_ProduceDecodableFile()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var samples = new short[sampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_wholebuffer_{Guid.NewGuid():N}.wma");

            try
            {
                WmaEncoder.Encode(outputPath, samples, channels, sampleRate);

                var streamInfo = WmaDecoder.Decode(outputPath, (_, _, _, _, _) => { });
                streamInfo.Channels.Should().Be(1);
                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.TotalSamples.Should().BeGreaterThan(0);
            }
            finally
            {
                File.Delete(outputPath);
            }
        }

        [Fact]
        public void OpenSession_WithUnsupportedChannelCount_Should_Throw()
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_invalid_{Guid.NewGuid():N}.wma");

            var act = () => WmaEncoderSession.OpenSession(outputPath, channels: 3, sampleRate: 44100);

            act.Should().ThrowExactly<NotSupportedException>();
            File.Exists(outputPath).Should().BeFalse();
        }

        [Fact]
        public void OpenSession_Stream_WithNullStream_Should_Throw()
        {
            var act = () => WmaEncoderSession.OpenSession((Stream)null!, channels: 1, sampleRate: 44100);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void OpenSession_Stream_WithUnsupportedChannelCount_Should_Throw()
        {
            using var destStream = new MemoryStream();

            var act = () => WmaEncoderSession.OpenSession(destStream, channels: 3, sampleRate: 44100);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void OpenSession_Stream_WithUnsupportedSampleRate_Should_Throw()
        {
            using var destStream = new MemoryStream();

            var act = () => WmaEncoderSession.OpenSession(destStream, channels: 1, sampleRate: 0);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void OpenSession_Stream_Should_RoundTrip_Exactly_SameAs_PathBased()
        {
            const int sampleRate = 44100;
            const int channels = 1;

            var samples = GenerateInterleavedTone(sampleRate, seconds: 1, channels);

            var pathBasedOutput = Path.Combine(Path.GetTempPath(), $"wma_path_{Guid.NewGuid():N}.wma");
            try
            {
                using (var pathSession = WmaEncoderSession.OpenSession(pathBasedOutput, channels, sampleRate))
                {
                    pathSession.WriteInterleavedSamples(samples, samples.Length / channels);
                    pathSession.Finish();
                }

                var pathBasedBytes = File.ReadAllBytes(pathBasedOutput);

                using var destStream = new MemoryStream();
                using (var streamSession = WmaEncoderSession.OpenSession(destStream, channels, sampleRate))
                {
                    streamSession.WriteInterleavedSamples(samples, samples.Length / channels);
                    streamSession.Finish();
                }

                // The ASF header embeds a fresh random file ID (Guid.NewGuid()) on every write, so
                // the two outputs can't be byte-identical start to finish -- decode both instead and
                // compare the real audio-relevant fields and content, the same bar
                // AssertRoundTripSnr's own callers already hold this codec to.
                destStream.Position = 0;
                var streamDecoded = new List<int>();
                var streamInfo = WmaDecoder.Decode(destStream, (block, _, _, _, _) => streamDecoded.AddRange(block.ToArray()));

                var pathDecoded = new List<int>();
                var pathStreamInfo = WmaDecoder.Decode(pathBasedOutput, (block, _, _, _, _) => pathDecoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(pathStreamInfo.Channels);
                streamInfo.SampleRate.Should().Be(pathStreamInfo.SampleRate);
                streamInfo.TotalSamples.Should().Be(pathStreamInfo.TotalSamples);
                streamDecoded.Should().Equal(pathDecoded, "the same PCM through the same encoder must decode to the same samples regardless of which OpenSession overload wrote it");
            }
            finally
            {
                File.Delete(pathBasedOutput);
            }
        }

        [Fact]
        public void OpenSession_Stream_WithNonSeekableDestination_Should_StillWork()
        {
            // Unlike FlacEncoder.OpenSession(Stream, ...), WmaEncoderSession needs no CanSeek at
            // all -- confirmed directly by actually writing through a destination that throws on
            // any Seek/Position access, not just by the absence of a CanSeek check in the source.
            const int sampleRate = 44100;
            var samples = GenerateInterleavedTone(sampleRate, seconds: 1, channels: 1);

            using var destStream = new NonSeekableWriteStream();
            using (var session = WmaEncoderSession.OpenSession(destStream, channels: 1, sampleRate))
            {
                session.WriteInterleavedSamples(samples, samples.Length);
                session.Finish();
            }
        }

        [Fact]
        public void OpenSession_Stream_Dispose_Should_NotDisposeCallerStream()
        {
            using var destStream = new MemoryStream();

            using (var session = WmaEncoderSession.OpenSession(destStream, channels: 1, sampleRate: 44100))
            {
                session.WriteInterleavedSamples([1, 2, 3], 3);
                session.Finish();
            }

            var act = () => destStream.Position;

            act.Should().NotThrow("the Stream-based overload must not dispose a stream the caller opened and owns, unlike the path-based overload's own FileStream");
        }

        private static int[] GenerateInterleavedTone(int sampleRate, int seconds, int channels)
        {
            var sampleCount = sampleRate * seconds;
            var samples = new int[sampleCount * channels];

            for (var i = 0; i < sampleCount; i++)
            {
                var value = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(i * channels) + channel] = value;
                }
            }

            return samples;
        }

        private static void AssertRoundTripSnr(string wmaPath, int[] originalInterleaved, int channels, int sampleRate, double minimumSnrDb)
        {
            var decodedSamples = new List<int>();
            var streamInfo = WmaDecoder.Decode(wmaPath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().NotBeEmpty();

            // The encoder primes its overlap-add with a silent lookback block, so the decoder's
            // output is delayed by exactly one frame relative to the original signal -- the same
            // inherent one-frame encoder delay documented for AacEncoderTest.
            var encoderDelaySamples = 1 << WmaTables.GetFrameLengthBits(sampleRate);

            for (var channel = 0; channel < channels; channel++)
            {
                var originalChannel = new List<double>();
                for (var i = channel; i < originalInterleaved.Length; i += channels)
                {
                    originalChannel.Add(originalInterleaved[i]);
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

                signalToNoiseRatioDb.Should().BeGreaterThan(minimumSnrDb, $"expected SNR > {minimumSnrDb}dB for channel {channel} (scale={scale}), got {signalToNoiseRatioDb}dB");
            }
        }

        [Fact]
        public void OpenSession_TwentyFourBitInput_Should_Encode_Identically_To_Equivalent_SixteenBitInput()
        {
            // Regression: the codec core only accepts 16-bit-range samples, and 24-bit input used
            // to reach it unscaled (wrapping/clipping into noise). A 24-bit sample that's exactly a
            // 16-bit one shifted left by 8 must now encode to the same audio. Compared after decode,
            // not byte-for-byte: AsfContainerWriter writes a fresh random file ID into every header.
            const int sampleRate = 44100;
            var sixteenBitSamples = new int[5000];
            for (var i = 0; i < sixteenBitSamples.Length; i++)
            {
                sixteenBitSamples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            var twentyFourBitSamples = sixteenBitSamples.Select(sample => sample << 8).ToArray();

            using var sixteenBitDest = new MemoryStream();
            using (var session = WmaEncoderSession.OpenSession(sixteenBitDest, channels: 1, sampleRate))
            {
                session.WriteInterleavedSamples(sixteenBitSamples, sixteenBitSamples.Length);
                session.Finish();
            }

            using var twentyFourBitDest = new MemoryStream();
            using (var session = WmaEncoderSession.OpenSession(twentyFourBitDest, channels: 1, sampleRate, bitsPerSample: 24))
            {
                session.WriteInterleavedSamples(twentyFourBitSamples, twentyFourBitSamples.Length);
                session.Finish();
            }

            DecodeAll(twentyFourBitDest).Should().Equal(DecodeAll(sixteenBitDest));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(12)]
        [InlineData(20)]
        public void OpenSession_UnsupportedBitsPerSample_Should_Throw_Before_Creating_File(int bitsPerSample)
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"wma_invalid_bits_{Guid.NewGuid():N}.wma");

            var pathAct = () => WmaEncoderSession.OpenSession(outputPath, channels: 1, sampleRate: 44100, bitsPerSample);
            var streamAct = () => WmaEncoderSession.OpenSession(new MemoryStream(), channels: 1, sampleRate: 44100, bitsPerSample);

            pathAct.Should().ThrowExactly<NotSupportedException>();
            streamAct.Should().ThrowExactly<NotSupportedException>();
            File.Exists(outputPath).Should().BeFalse("validation should fail before any file is created");
        }

        private static List<int> DecodeAll(MemoryStream encoded)
        {
            encoded.Position = 0;
            var decoded = new List<int>();
            WmaDecoder.Decode(encoded, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));
            return decoded;
        }
    }
}
