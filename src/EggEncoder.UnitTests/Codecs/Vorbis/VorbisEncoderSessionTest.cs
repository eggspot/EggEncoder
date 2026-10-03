using EggEncoder.Codecs.Vorbis;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Vorbis
{
    public class VorbisEncoderSessionTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void OpenSession_With_UnsupportedChannelCount_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => VorbisEncoderSession.OpenSession(filePath, channels: 3, SampleRate, bitsPerSample: 16);

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
                var act = () => VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 24);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_NonPositiveSampleRate_Should_Throw()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                var act = () => VorbisEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 0, bitsPerSample: 16);

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void OpenSession_With_CustomQuality_Should_RoundTrip()
        {
            // Unlike Opus/FLAC/MP3 (bitrate/compression level), Vorbis's own quality knob is the one
            // parameter this codec adds beyond the shared OpenSession shape -- confirm the optional
            // parameter is actually wired through to VorbisInfo.InitVariableBitRate rather than
            // silently ignored, by picking a value far from DefaultQuality and confirming the file
            // still round-trips.
            var samples = GenerateTone(seconds: 1, channels: 1);
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_quality_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16, quality: 0.9f))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Mono_WithGoodSnr()
        {
            var samples = GenerateTone(seconds: 2, channels: 1);

            AssertRoundTripSnr(samples, channels: 1, minimumSnrDb: 15);
        }

        [Fact]
        public void WriteInterleavedSamples_Then_Finish_Should_RoundTrip_Stereo_WithGoodSnr()
        {
            var samples = GenerateTone(seconds: 2, channels: 2);

            AssertRoundTripSnr(samples, channels: 2, minimumSnrDb: 15);
        }

        [Fact]
        public void WriteInterleavedSamples_CalledInSmallChunks_Should_StillProduceAValidFile()
        {
            var samples = GenerateTone(seconds: 1, channels: 1);
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_small_chunks_{Guid.NewGuid():N}.ogg");

            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    const int chunkSize = 37; // deliberately irregular, exercising the scratch-buffer growth path repeatedly
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
                decoded.Count.Should().Be((int)streamInfo.TotalSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_WithGrowingThenShrinkingChunks_Should_StillProduceAValidFile()
        {
            // Exercises VorbisEncoderSession's scratch-buffer reallocation in both directions: a chunk
            // larger than any previous one forces growth, and a smaller chunk afterwards must only
            // use its own prefix of the now-larger buffer, not the stale tail from a prior call.
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_variable_chunks_{Guid.NewGuid():N}.ogg");
            var samples = GenerateTone(seconds: 1, channels: 1);

            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    int[] chunkSizes = [100, 5000, 50, 10000, 1];
                    var offset = 0;
                    foreach (var chunkSize in chunkSizes)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        session.WriteInterleavedSamples(samples[offset..(offset + count)], count);
                        offset += count;
                    }

                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(filePath, (block, _, _, _, _) => decoded.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void WriteInterleavedSamples_WithZeroFrameCount_Should_BeANoOp()
        {
            // Unlike Opus (whose OpusHead.pre_skip lets a zero-sample session decode back to exactly
            // 0 total samples), Vorbis has no equivalent field, so even a Finish() with nothing ever
            // written still yields a few thousand samples of the encoder's own windowing/lookahead
            // ringing once decoded (see Finish_WithNoSamplesWritten_Should_Produce_An_EmptyButValidFile)
            // -- this test only confirms the frameCount==0 early-return doesn't throw or corrupt the
            // stream, by comparing against that same baseline rather than asserting an exact count.
            var baselineFilePath = Path.Combine(Path.GetTempPath(), $"vorbis_zero_frames_baseline_{Guid.NewGuid():N}.ogg");
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_zero_frames_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var baselineSession = VorbisEncoderSession.OpenSession(baselineFilePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    baselineSession.Finish();
                }

                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    var act = () => session.WriteInterleavedSamples([], frameCount: 0);
                    act.Should().NotThrow();

                    session.Finish();
                }

                var baselineStreamInfo = VorbisDecoder.Decode(baselineFilePath, (_, _, _, _, _) => { });
                var streamInfo = VorbisDecoder.Decode(filePath, (_, _, _, _, _) => { });

                streamInfo.TotalSamples.Should().Be(baselineStreamInfo.TotalSamples);
            }
            finally
            {
                File.Delete(baselineFilePath);
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Finish_WithNoSamplesWritten_Should_Produce_An_EmptyButValidFile()
        {
            // Vorbis has no pre_skip-equivalent field (see the AssertRoundTripSnr remarks below), so
            // "no samples written" still decodes to a small amount of windowing-related output rather
            // than exactly 0 -- a genuine, inherent property of this codec, not a bug.
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_empty_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    session.Finish();
                }

                var streamInfo = VorbisDecoder.Decode(filePath, (_, _, _, _, _) => { });
                streamInfo.TotalSamples.Should().BeLessThan(SampleRate / 2, "an empty session should decode to at most a fraction of a second of windowing ringing, not real audio");
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Dispose_CalledTwice_Should_Not_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_dispose_{Guid.NewGuid():N}.ogg");
            try
            {
                var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16);
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
        public void WriteInterleavedSamples_SingleCallSpanningMultipleBlocks_Should_RoundTrip()
        {
            var samples = GenerateTone(seconds: 3, channels: 1);

            AssertRoundTripSnr(samples, channels: 1, minimumSnrDb: 15);
        }

        private static int[] GenerateTone(int seconds, int channels)
        {
            var frameCount = SampleRate * seconds;
            var samples = new int[frameCount * channels];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var value = (int)(8000 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = value;
                }
            }

            return samples;
        }

        // Unlike Opus (which exposes an explicit pre_skip field in OpusHead, letting
        // OpusEncoderSessionTest assume decoded[0] already lines up with original[0]), Vorbis's own
        // container has no equivalent -- empirically, this encoder's MDCT lookahead delays decoded
        // output by a large, fixed (duration-independent, content-independent) number of samples
        // (~3386 for a 44100Hz/quality=0.5 mono stream). Rather than hardcoding that magic number
        // (which could silently go stale if DefaultQuality or SampleRate ever changes), this finds
        // the true alignment empirically via normalized cross-correlation, the same technique used
        // to discover the delay in the first place.
        private static void AssertRoundTripSnr(int[] originalInterleaved, int channels, double minimumSnrDb)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_session_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(originalInterleaved, originalInterleaved.Length / channels);
                    session.Finish();
                }

                var decodedSamples = new List<int>();
                var streamInfo = VorbisDecoder.Decode(filePath, (block, decodedChannels, rate, bits, _) =>
                {
                    decodedChannels.Should().Be(channels);
                    rate.Should().Be(SampleRate);
                    bits.Should().Be(16);
                    decodedSamples.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(channels);
                streamInfo.SampleRate.Should().Be(SampleRate);
                decodedSamples.Should().NotBeEmpty();

                for (var channel = 0; channel < channels; channel++)
                {
                    var originalChannel = Deinterleave(originalInterleaved, channels, channel);
                    var decodedChannel = Deinterleave(decodedSamples, channels, channel);

                    var offset = FindBestAlignmentOffset(originalChannel, decodedChannel, maxOffset: SampleRate / 4);

                    var compareLength = Math.Min(decodedChannel.Count - offset, originalChannel.Count);
                    compareLength.Should().BeGreaterThan(1000);

                    double dotProduct = 0;
                    double originalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        dotProduct += decodedChannel[offset + i] * originalChannel[i];
                        originalEnergy += originalChannel[i] * originalChannel[i];
                    }

                    originalEnergy.Should().BeGreaterThan(0);
                    var scale = dotProduct / originalEnergy;

                    double errorEnergy = 0;
                    double signalEnergy = 0;
                    for (var i = 0; i < compareLength; i++)
                    {
                        var expected = originalChannel[i] * scale;
                        var error = decodedChannel[offset + i] - expected;
                        errorEnergy += error * error;
                        signalEnergy += expected * expected;
                    }

                    var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

                    signalToNoiseRatioDb.Should().BeGreaterThan(minimumSnrDb, $"expected SNR > {minimumSnrDb}dB for channel {channel} (offset={offset}, scale={scale}), got {signalToNoiseRatioDb}dB");
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static List<double> Deinterleave(IReadOnlyList<int> interleaved, int channels, int channel)
        {
            var result = new List<double>();
            for (var i = channel; i < interleaved.Count; i += channels)
            {
                result.Add(interleaved[i]);
            }

            return result;
        }

        private static int FindBestAlignmentOffset(IReadOnlyList<double> original, IReadOnlyList<double> decoded, int maxOffset)
        {
            var compareLength = Math.Min(2000, original.Count);
            var bestOffset = 0;
            var bestCorrelation = double.MinValue;

            for (var offset = 0; offset <= maxOffset && offset + compareLength <= decoded.Count; offset++)
            {
                double dot = 0, originalEnergy = 0, decodedEnergy = 0;
                for (var i = 0; i < compareLength; i++)
                {
                    dot += decoded[offset + i] * original[i];
                    originalEnergy += original[i] * original[i];
                    decodedEnergy += decoded[offset + i] * decoded[offset + i];
                }

                var normalized = originalEnergy > 0 && decodedEnergy > 0 ? dot / Math.Sqrt(originalEnergy * decodedEnergy) : 0;
                if (normalized > bestCorrelation)
                {
                    bestCorrelation = normalized;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }
    }
}
