using EggEncoder.Codecs.Vorbis;
using FluentAssertions;
using OggVorbisEncoder;

namespace EggEncoder.UnitTests.Codecs.Vorbis
{
    public class VorbisDecoderTest
    {
        private const int SampleRate = 44100;

        [Fact]
        public void Decode_StereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var samples = GenerateTone(seconds: 1, channels: 2);
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_decoder_stereo_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 2, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length / 2);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = VorbisDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, _) =>
                {
                    channels.Should().Be(2);
                    sampleRate.Should().Be(SampleRate);
                    bitsPerSample.Should().Be(16);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                decoded.Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_Should_Invoke_The_Callback_MultipleTimes_With_Consistent_Metadata()
        {
            var samples = GenerateTone(seconds: 3, channels: 1);
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_decoder_multi_{Guid.NewGuid():N}.ogg");
            try
            {
                using (var session = VorbisEncoderSession.OpenSession(filePath, channels: 1, SampleRate, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var callbackCount = 0;
                long? firstReportedTotal = null;
                var totalDecodedFrames = 0L;

                var streamInfo = VorbisDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    callbackCount++;
                    channels.Should().Be(1);
                    sampleRate.Should().Be(SampleRate);
                    bitsPerSample.Should().Be(16);

                    firstReportedTotal ??= totalSamples;
                    totalSamples.Should().Be(firstReportedTotal, "every callback should report the same, already-known total");

                    totalDecodedFrames += block.Length;
                });

                callbackCount.Should().BeGreaterThan(1);
                totalDecodedFrames.Should().Be(streamInfo.TotalSamples);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithUnsupportedChannelCount_Should_Throw()
        {
            // VorbisEncoderSession.OpenSession itself blocks anything but mono/stereo, so to exercise
            // VorbisDecoder's own matching guard this builds a genuine 3-channel Ogg Vorbis file
            // directly through OggVorbisEncoder's own API (the README documents multichannel as
            // supported, just "non-optimal") -- a real file NVorbis can open and report
            // Channels == 3 for, rather than a hand-corrupted byte that might fail for an unrelated
            // reason (e.g. a checksum mismatch) before ever reaching this decoder's own check.
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_decoder_3ch_{Guid.NewGuid():N}.ogg");
            try
            {
                CreateMultiChannelFile(filePath, channels: 3);

                var act = () => VorbisDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_WithMissingOrMalformedHeaders_Should_Throw()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"vorbis_decoder_malformed_{Guid.NewGuid():N}.ogg");
            try
            {
                File.WriteAllBytes(filePath, []);

                var act = () => VorbisDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().Throw<Exception>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static void CreateMultiChannelFile(string filePath, int channels)
        {
            const int serialNumber = 1;
            using var destStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

            var info = VorbisInfo.InitVariableBitRate(channels, SampleRate, VorbisEncoderSession.DefaultQuality);
            var oggStream = new OggStream(serialNumber);
            var processingState = ProcessingState.Create(info);

            var comments = new Comments();
            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
            FlushPages(oggStream, destStream, force: true);

            var channelBuffers = new float[channels][];
            const int frameCount = 1024;
            for (var c = 0; c < channels; c++)
            {
                channelBuffers[c] = new float[frameCount];
            }

            processingState.WriteData(channelBuffers, frameCount, 0);
            DrainPackets(oggStream, processingState, destStream);

            processingState.WriteEndOfStream();
            DrainPackets(oggStream, processingState, destStream);
            FlushPages(oggStream, destStream, force: true);
        }

        private static void DrainPackets(OggStream oggStream, ProcessingState processingState, FileStream destStream)
        {
            while (!oggStream.Finished && processingState.PacketOut(out var packet))
            {
                oggStream.PacketIn(packet);
                FlushPages(oggStream, destStream, force: false);
            }
        }

        private static void FlushPages(OggStream oggStream, FileStream destStream, bool force)
        {
            while (oggStream.PageOut(out var page, force))
            {
                destStream.Write(page.Header, 0, page.Header.Length);
                destStream.Write(page.Body, 0, page.Body.Length);
            }
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
    }
}
