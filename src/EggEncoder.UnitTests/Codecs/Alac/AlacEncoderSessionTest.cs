using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacEncoderSessionTest
    {
        [Fact]
        public void OpenSession_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => AlacEncoderSession.OpenSession(filePath, channels: 3, sampleRate: 44100, bitsPerSample: 16);

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
                var act = () => AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 20);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_TwentyFourBit_FullScale()
        {
            // Full 24-bit signed range (-8388608..8388607), not just 16-bit-scale values -- confirms
            // the Rice coder's escape width (predictionBitsPerSample = BitDepth + channels - 1 = 24
            // for mono) genuinely widens rather than silently truncating/overflowing at 16-bit scale.
            const int sampleRate = 44100;
            const int sampleCount = sampleRate * 2;

            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (int)(8_000_000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            samples[0] = -8_388_608;
            samples[1] = 8_388_607;

            AssertRoundTrips(samples, sampleRate, bitsPerSample: 24);
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_TwentyFourBit_Stereo()
        {
            // Correlated left/right content at 24-bit, round-tripped through the real session and CAF
            // container (not just EncodePacket/DecodePacket directly) -- whichever of EncodePacket's
            // mixed/independent/verbatim candidates this data actually lands on (confirmed to be the
            // mid/side mix by AlacFrameEncoderTest's own dedicated 24-bit bit-level check), this proves
            // the full file-level pipeline round-trips correctly at the wider predictionBitsPerSample.
            const int sampleRate = 44100;
            const int frameCount = sampleRate * 2;

            var interleaved = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                var value = (int)(8_000_000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                interleaved[i * 2] = value; // left
                interleaved[(i * 2) + 1] = value + 1000; // right, highly correlated with left
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_session_24bit_stereo_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 2, sampleRate, bitsPerSample: 24))
                {
                    session.WriteInterleavedSamples(interleaved, frameCount);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, rate, bits, total) =>
                {
                    channels.Should().Be(2);
                    rate.Should().Be(sampleRate);
                    bits.Should().Be(24);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                streamInfo.BitsPerSample.Should().Be(24);
                streamInfo.TotalSamples.Should().Be(frameCount);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_AcrossMultipleFrames()
        {
            // 44100 * 2 samples spans more than ten 4096-sample ALAC frames, with a short final frame.
            const int sampleRate = 44100;
            const int sampleCount = sampleRate * 2;

            var samples = new int[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            }

            AssertRoundTrips(samples, sampleRate);
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_ExactMultipleOfFrameLength()
        {
            var samples = new int[4096 * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 100;
            }

            AssertRoundTrips(samples, sampleRate: 44100);
        }

        [Fact]
        public void WriteInterleavedSamples_CalledInSmallChunks_Should_StillAccumulateIntoFullFrames()
        {
            var samples = new int[10000];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 50;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_small_chunks_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    const int chunkSize = 37; // deliberately not a divisor of the ALAC frame length (4096)
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, rate, bits, total) => decoded.AddRange(block.ToArray()));

                streamInfo.TotalSamples.Should().Be(samples.Length);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Finish_WithNoSamplesWritten_Should_Produce_An_EmptyButValidFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"alac_empty_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.Finish();
                }

                var streamInfo = AlacDecoder.Decode(filePath, (_, _, _, _, _) => { });
                streamInfo.TotalSamples.Should().Be(0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Stereo_AcrossMultipleFrames()
        {
            const int sampleRate = 44100;
            const int frameCount = sampleRate * 2;

            var interleaved = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                interleaved[i * 2] = (int)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)); // left
                interleaved[(i * 2) + 1] = (int)(10000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate)); // right, different tone
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_session_stereo_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 2, sampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(interleaved, frameCount);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, rate, bits, total) =>
                {
                    channels.Should().Be(2);
                    rate.Should().Be(sampleRate);
                    bits.Should().Be(16);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                streamInfo.TotalSamples.Should().Be(frameCount);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Stereo_CalledInSmallChunks_Should_StillDeinterleaveCorrectly()
        {
            const int frameCount = 10000;
            var interleaved = new int[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                interleaved[i * 2] = i % 50;
                interleaved[(i * 2) + 1] = -(i % 70);
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_session_stereo_chunks_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16))
                {
                    const int chunkFrames = 13; // deliberately not a divisor of the ALAC frame length (4096)
                    for (var frameOffset = 0; frameOffset < frameCount; frameOffset += chunkFrames)
                    {
                        var framesThisCall = Math.Min(chunkFrames, frameCount - frameOffset);
                        var sampleOffset = frameOffset * 2;
                        session.WriteInterleavedSamples(interleaved[sampleOffset..(sampleOffset + (framesThisCall * 2))], framesThisCall);
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.TotalSamples.Should().Be(frameCount);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Dispose_CalledTwice_Should_Not_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"alac_dispose_{Guid.NewGuid():N}.caf");
            try
            {
                var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 44100, bitsPerSample: 16);
                session.Dispose();

                var act = session.Dispose;
                act.Should().NotThrow();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void AssertRoundTrips(int[] samples, int sampleRate, int bitsPerSample = 16)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"alac_session_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate, bitsPerSample))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, rate, bits, total) =>
                {
                    channels.Should().Be(1);
                    rate.Should().Be(sampleRate);
                    bits.Should().Be(bitsPerSample);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.SampleRate.Should().Be(sampleRate);
                streamInfo.Channels.Should().Be(1);
                streamInfo.BitsPerSample.Should().Be(bitsPerSample);
                streamInfo.TotalSamples.Should().Be(samples.Length);
                decoded.Should().Equal(samples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
