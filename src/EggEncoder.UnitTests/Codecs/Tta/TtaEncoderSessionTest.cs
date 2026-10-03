using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaEncoderSessionTest
    {
        [Fact]
        public void OpenSession_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => TtaEncoderSession.OpenSession(filePath, channels: 3, sampleRate: 44100, bitsPerSample: 16);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_NonSixteenBitDepth_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 24);

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
            // A zero sample rate would otherwise make _frameLength compute to 0, which turns
            // WriteInterleavedSamples' chunking loop into an infinite loop the moment any samples
            // are written (every iteration copies 0 samples, so bufferOffset never advances).
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 0, bitsPerSample: 16);

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
                var act = () => TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: -1, bitsPerSample: 16);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_AcrossMultipleFrames()
        {
            const int sampleRate = 44100;
            const int sampleCount = sampleRate * 2;

            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            AssertRoundTrips(samples, channels: 1, sampleRate);
        }

        [Fact]
        public void WriteInterleavedSamples_Stereo_Then_Finish_Should_RoundTrip_AcrossMultipleFrames()
        {
            const int sampleRate = 44100;
            const int frameCount = sampleRate * 2;

            var interleaved = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                interleaved[i * 2] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                interleaved[(i * 2) + 1] = (int)(8000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
            }

            AssertRoundTrips(interleaved, channels: 2, sampleRate);
        }

        [Fact]
        public void WriteInterleavedSamples_CalledInSmallChunks_Should_StillAccumulateIntoFullFrames()
        {
            var samples = new int[10000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 50;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"tta_small_chunks_{Guid.NewGuid():N}.tta");
            try
            {
                using (var session = TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    const int chunkSize = 37; // deliberately not a divisor of TTA's frame length (~46079 at 44100Hz)
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(filePath, (block, channels, rate, bits, total) => decoded.AddRange(block.ToArray()));

                streamInfo.TotalSamples.Should().Be(samples.Length);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_SingleCallSpanningMultipleFrames_Should_RoundTrip()
        {
            // At sampleRate=245, TtaEncoderSession's frame length is exactly 256 -- one single
            // WriteInterleavedSamples call spanning 700 samples forces FlushPendingFrame to fire more
            // than once from inside one call, not just once per call like the chunked test above.
            var samples = new int[700];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (i % 200) - 100;
            }

            AssertRoundTrips(samples, channels: 1, sampleRate: 245);
        }

        [Fact]
        public void Finish_WithNoSamplesWritten_Should_Produce_An_EmptyButValidFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_empty_{Guid.NewGuid():N}.tta");
            try
            {
                using (var session = TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.Finish();
                }

                var streamInfo = TtaDecoder.Decode(filePath, (_, _, _, _, _) => { });
                streamInfo.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Dispose_CalledTwice_Should_Not_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_dispose_{Guid.NewGuid():N}.tta");
            try
            {
                var session = TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16);
                session.Dispose();

                var act = session.Dispose;
                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void AssertRoundTrips(int[] interleavedSamples, int channels, int sampleRate)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_session_{Guid.NewGuid():N}.tta");
            try
            {
                using (var session = TtaEncoderSession.OpenSession(filePath, channels, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(interleavedSamples, interleavedSamples.Length / channels);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(filePath, (block, decodedChannels, rate, bits, total) =>
                {
                    decodedChannels.Should().Be(channels);
                    rate.Should().Be(sampleRate);
                    bits.Should().Be(16);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.Channels.Should().Be(channels);
                streamInfo.BitsPerSample.Should().Be(16);
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
