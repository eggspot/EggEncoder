using EggEncoder.Codecs.WavPack;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackEncoderSessionTest
    {
        [Fact]
        public void OpenSession_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 3, bitsPerSample: 16, sampleRate: 44100, totalSamples: 0);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_UnsupportedBitDepth_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 8, sampleRate: 44100, totalSamples: 0);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_ZeroSampleRate_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 0, totalSamples: 0);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_NegativeSampleRate_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: -1, totalSamples: 0);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Mono16Bit()
        {
            const int sampleRate = 44100;
            const int sampleCount = sampleRate * 2;

            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            AssertRoundTrips(samples, channels: 1, sampleRate, bitsPerSample: 16);
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Stereo24Bit()
        {
            const int sampleRate = 48000;
            const int frameCount = sampleRate * 2;

            var interleaved = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                interleaved[i * 2] = (int)(1000000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                interleaved[(i * 2) + 1] = (int)(800000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
            }

            AssertRoundTrips(interleaved, channels: 2, sampleRate, bitsPerSample: 24);
        }

        [Fact]
        public void WriteInterleavedSamples_CalledInSmallChunks_Should_StillProduceAnExactRoundTrip()
        {
            var samples = new int[10000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 50;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_small_chunks_{Guid.NewGuid():N}.wv");
            try
            {
                using (var session = WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: samples.Length))
                {
                    const int chunkSize = 37; // deliberately irregular, exercising repeated small PackSamples calls
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                    }

                    session.Finish();
                }

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(filePath);

                streamInfo.TotalSamples.Should().Be(samples.Length);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_WithZeroFrameCount_Should_BeANoOp()
        {
            var samples = new[] { 1, 2, 3, 4 };
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_zero_frames_{Guid.NewGuid():N}.wv");
            try
            {
                using (var session = WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: samples.Length))
                {
                    var act = () => session.WriteInterleavedSamples([], frameCount: 0);
                    act.Should().NotThrow();

                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(filePath);

                streamInfo.TotalSamples.Should().Be(samples.Length);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_ZeroTotalSamples_Should_Throw()
        {
            // Unlike FLAC/TTA/Opus/Vorbis, WavPack genuinely cannot represent an empty/zero-sample
            // stream -- confirmed from WavPack's own reference CLI (cli/wavpack.c), which refuses to
            // encode one outright ("no raw PCM data to encode!"), and independently reconfirmed via a
            // real CI failure: WavpackSetConfiguration64 itself rejects total_samples == 0, and
            // substituting -1 ("unknown") produces a file WavpackOpenFileInput then refuses to read
            // back. See OpenSession's own doc comment for the full chain of evidence.
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: 0);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_NegativeTotalSamples_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: -5);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Dispose_CalledTwice_Should_Not_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_dispose_{Guid.NewGuid():N}.wv");
            try
            {
                var session = WavPackEncoderSession.OpenSession(filePath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: 1);
                session.Dispose();

                var act = session.Dispose;
                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_InvalidDestinationPath_Should_Throw()
        {
            // An invalid destination path fails at File.Create, before the try/catch that cleans up
            // the native context and GCHandle even starts -- this confirms that early failure
            // propagates as the real underlying exception, not wrapped or masked by anything else.
            // It does NOT exercise that try/catch's own cleanup logic at all (there's nothing yet to
            // clean up at this point) -- no test currently forces WavpackSetConfiguration64 or
            // WavpackPackInit to fail on an otherwise-valid config to exercise that path for real.
            var invalidPath = Path.Combine(Path.GetTempPath(), $"wavpack_missing_dir_{Guid.NewGuid():N}", "dest.wv");

            var act = () => WavPackEncoderSession.OpenSession(invalidPath, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: 1);

            act.Should().ThrowExactly<DirectoryNotFoundException>();
        }

        private static void AssertRoundTrips(int[] interleavedSamples, int channels, int sampleRate, int bitsPerSample)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"wavpack_session_{Guid.NewGuid():N}.wv");
            try
            {
                using (var session = WavPackEncoderSession.OpenSession(filePath, channels, bitsPerSample, sampleRate, interleavedSamples.Length / channels))
                {
                    session.WriteInterleavedSamples(interleavedSamples, interleavedSamples.Length / channels);
                    session.Finish();
                }

                var (streamInfo, decoded) = WavPackTestDecoder.DecodeAll(filePath);

                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.Channels.Should().Be(channels);
                streamInfo.BitsPerSample.Should().Be(bitsPerSample);
                streamInfo.TotalSamples.Should().Be(interleavedSamples.Length / channels);
                decoded.Should().Equal(interleavedSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
