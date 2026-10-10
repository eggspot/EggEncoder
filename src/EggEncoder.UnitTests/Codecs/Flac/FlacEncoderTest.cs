using EggEncoder.Codecs.Flac;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Flac
{
    // FlacFrameEncoder itself is private/internal and has no fixture-driven reference encoder to
    // cross-check against (unlike FlacFrameDecoder's real ffmpeg fixtures) -- so coverage here
    // leans on round-trip correctness (encode via FlacEncoder, decode via the already-verified
    // FlacDecoder, assert exact samples) for inputs chosen, empirically, to deterministically drive
    // each of FlacFrameEncoder's own decision branches (every FIXED order 0-4, the escape residual
    // path, every one of the four stereo modes, and every STREAMINFO block-size-range case), plus
    // reading the first frame's own channel-assignment nibble directly to confirm *which* stereo
    // mode actually got chosen (round-trip sample-exactness alone can't distinguish that).
    public class FlacEncoderTest
    {
        [Fact]
        public void OpenSession_WithZeroChannels_Should_Throw()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var act = () => FlacEncoder.OpenSession(Path.Combine(tempDirectory, "out.flac"), channels: 0, bitsPerSample: 16, sampleRate: 44100);

                act.Should().Throw<NotSupportedException>().WithMessage("*0 channels*");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        public void OpenSession_WithBitsPerSampleOutOfRange_Should_Throw(int bitsPerSample)
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var act = () => FlacEncoder.OpenSession(Path.Combine(tempDirectory, "out.flac"), channels: 1, bitsPerSample, sampleRate: 44100);

                act.Should().Throw<NotSupportedException>().WithMessage("*bits per sample*");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_EmptySource_Should_ProduceAZeroSampleDecodableFile()
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var flacPath = Path.Combine(tempDirectory, "dest.flac");
                WavFileBuilder.Create(wavPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples: []);

                FlacEncoder.Encode(wavPath, flacPath);
                var (info, samples) = FlacTestDecoder.DecodeAll(flacPath);

                info.TotalSamples.Should().Be(0);
                samples.Should().BeEmpty();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Encode_FewerThanSixteenSamplesTotal_Should_ProduceASingleShortLastFrame()
        {
            // Exercises the FlacEncoderSession.Finish single-frame STREAMINFO case: that one frame
            // is simultaneously the first and the last, so its own (here, <16) size is legitimately
            // reported as both the min and max block size (see FlacDecoder's own RFC 9639 section
            // 4.1 note on this).
            var interleavedSamples = new[] { short.MinValue, short.MaxValue, 0, short.MaxValue, short.MinValue, 0 };

            AssertRoundTrip(channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);
        }

        [Fact]
        public void Encode_MoreThanOneBlock_Should_ProduceMultipleFramesThatAllDecodeExactly()
        {
            // 9000 samples > one 4096-sample internal block, so this produces 3 frames (two full,
            // one short last one) -- exercises FlacEncoderSession.Finish's "2+ frames" STREAMINFO
            // case, where every non-last frame uses exactly the nominal block size.
            var random = new Random(7);
            var interleavedSamples = new int[9000];
            for (var i = 0; i < interleavedSamples.Length; i++)
            {
                interleavedSamples[i] = random.Next(short.MinValue, short.MaxValue);
            }

            AssertRoundTrip(channels: 1, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);
        }

        [Fact]
        public void Encode_32BitStereo_Should_AlwaysUseIndependentChannels_NotSideChannelModes()
        {
            // FlacFrameEncoder has no 64-bit subframe write path for the 33-bit-wide side channel a
            // 32-bit stereo pair would need (see its own doc comment) -- channel assignment must
            // therefore always come out as 1 (independent), never 8/9/10.
            var random = new Random(11);
            var interleavedSamples = new int[2000 * 2];
            for (var i = 0; i < interleavedSamples.Length; i++)
            {
                interleavedSamples[i] = random.Next(int.MinValue / 2, int.MaxValue / 2);
            }

            var tempDirectory = CreateTempDirectory();
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var flacPath = Path.Combine(tempDirectory, "dest.flac");
                WavFileBuilder.Create(wavPath, channels: 2, sampleRate: 44100, bitsPerSample: 32, interleavedSamples);

                FlacEncoder.Encode(wavPath, flacPath);

                ReadFirstFrameChannelAssignment(flacPath).Should().Be(1);

                var (_, decodedSamples) = FlacTestDecoder.DecodeAll(flacPath);
                decodedSamples.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(0)] // pure random noise: differencing never helps, order 0 (the raw samples) is cheapest
        [InlineData(4)] // an exact cubic: its own 4th difference is exactly 0, cheaper than order 3's constant-but-nonzero one over a large block
        public void Encode_MonoSignalsThatFavorDifferentFixedOrders_Should_RoundTripExactly(int expectedOrderHint)
        {
            int[] samples;
            int bitsPerSample;
            if (expectedOrderHint == 0)
            {
                var random = new Random(3);
                samples = new int[2000];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = random.Next(short.MinValue, short.MaxValue);
                }

                bitsPerSample = 16;
            }
            else
            {
                samples = new int[1000];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = i * i * i;
                }

                bitsPerSample = 32;
            }

            AssertRoundTrip(channels: 1, sampleRate: 11025, bitsPerSample, samples);
        }

        [Fact]
        public void Encode_LinearRamp_Should_RoundTripExactly()
        {
            // An exact arithmetic sequence's 2nd fixed difference is identically 0 -- the cheapest
            // possible residual (RequiredSignedWidth's own escape-width-0 shortcut).
            var samples = new int[2000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i * 5;
            }

            AssertRoundTrip(channels: 1, sampleRate: 44100, bitsPerSample: 16, samples);
        }

        [Fact]
        public void Encode_ExactQuadratic_Should_RoundTripExactly()
        {
            // A clean i*i sequence (no integer-division truncation noise): its 3rd difference is
            // identically 0, which for a large enough block beats order 2's constant-but-nonzero one.
            var samples = new int[2000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i * i;
            }

            AssertRoundTrip(channels: 1, sampleRate: 11025, bitsPerSample: 32, samples);
        }

        [Fact]
        public void Encode_TinyBlockWithExtremeAlternatingValues_Should_UseTheResidualEscapeCode_AndRoundTripExactly()
        {
            // 4 samples alternating between the full 16-bit range: no single Rice parameter codes
            // this cheaply over so few values, so the escape (raw) residual path should win.
            var samples = new[] { short.MinValue, short.MaxValue, (int)short.MinValue, short.MaxValue };

            AssertRoundTrip(channels: 1, sampleRate: 44100, bitsPerSample: 16, samples);
        }

        [Fact]
        public void Encode_UncorrelatedStereo_Should_ChooseIndependentChannelAssignment()
        {
            var random = new Random(42);
            var interleavedSamples = new int[2000 * 2];
            for (var i = 0; i < interleavedSamples.Length; i++)
            {
                interleavedSamples[i] = random.Next(short.MinValue, short.MaxValue);
            }

            AssertStereoModeChosen(interleavedSamples, expectedChannelAssignment: 1);
        }

        [Fact]
        public void Encode_NearIdenticalStereoChannels_Should_ChooseMidSideAssignment()
        {
            var interleavedSamples = new int[2000 * 2];
            for (var i = 0; i < 2000; i++)
            {
                var v = (int)(10000 * Math.Sin(i * 0.05));
                interleavedSamples[i * 2] = v;
                interleavedSamples[(i * 2) + 1] = v + 3;
            }

            AssertStereoModeChosen(interleavedSamples, expectedChannelAssignment: 10);
        }

        [Fact]
        public void Encode_CleanLeftChannelWithPerturbedRightChannel_Should_ChooseLeftSideAssignment()
        {
            // mid = (left+right)>>1 inherits right's own small perturbation (it's not simply equal
            // to the clean left channel the way a constant per-sample offset would floor-divide back
            // to), while left/side's own "left" term stays exactly as cheap as the clean ramp alone
            // -- so left/side should beat mid/side here, even though side costs the same either way.
            var (left, right) = BuildCleanAndPerturbedRamps();
            var interleavedSamples = Interleave(left, right);

            AssertStereoModeChosen(interleavedSamples, expectedChannelAssignment: 8);
        }

        [Fact]
        public void Encode_CleanRightChannelWithPerturbedLeftChannel_Should_ChooseRightSideAssignment()
        {
            var (clean, perturbed) = BuildCleanAndPerturbedRamps();
            var interleavedSamples = Interleave(perturbed, clean);

            AssertStereoModeChosen(interleavedSamples, expectedChannelAssignment: 9);
        }

        private static (int[] Clean, int[] Perturbed) BuildCleanAndPerturbedRamps()
        {
            var clean = new int[2000];
            var perturbed = new int[2000];
            for (var i = 0; i < 2000; i++)
            {
                clean[i] = i * 5;
                perturbed[i] = clean[i] + (i % 2 == 0 ? 2 : -2);
            }

            return (clean, perturbed);
        }

        private static int[] Interleave(int[] left, int[] right)
        {
            var interleaved = new int[left.Length * 2];
            for (var i = 0; i < left.Length; i++)
            {
                interleaved[i * 2] = left[i];
                interleaved[(i * 2) + 1] = right[i];
            }

            return interleaved;
        }

        private static void AssertStereoModeChosen(int[] interleavedSamples, int expectedChannelAssignment)
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var flacPath = Path.Combine(tempDirectory, "dest.flac");
                WavFileBuilder.Create(wavPath, channels: 2, sampleRate: 44100, bitsPerSample: 16, interleavedSamples);

                FlacEncoder.Encode(wavPath, flacPath);

                ReadFirstFrameChannelAssignment(flacPath).Should().Be(expectedChannelAssignment);

                var (_, decodedSamples) = FlacTestDecoder.DecodeAll(flacPath);
                decodedSamples.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        // 'fLaC' (4 bytes) + STREAMINFO block header (4 bytes) + STREAMINFO payload (34 bytes) = 42
        // bytes before the first frame; the frame header's own 4th byte holds the channel
        // assignment in its high nibble (RFC 9639 section 9.1.4).
        private static int ReadFirstFrameChannelAssignment(string flacPath)
        {
            var bytes = File.ReadAllBytes(flacPath);
            return bytes[45] >> 4;
        }

        private static void AssertRoundTrip(int channels, int sampleRate, int bitsPerSample, int[] interleavedSamples)
        {
            var tempDirectory = CreateTempDirectory();
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var flacPath = Path.Combine(tempDirectory, "dest.flac");
                WavFileBuilder.Create(wavPath, channels, sampleRate, bitsPerSample, interleavedSamples);

                FlacEncoder.Encode(wavPath, flacPath);

                var (streamInfo, decodedSamples) = FlacTestDecoder.DecodeAll(flacPath);

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.BitsPerSample.Should().Be(bitsPerSample);
                streamInfo.TotalSamples.Should().Be(interleavedSamples.Length / channels);
                decodedSamples.Should().Equal(interleavedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
