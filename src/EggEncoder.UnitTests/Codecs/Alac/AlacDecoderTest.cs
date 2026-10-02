using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacDecoderTest
    {
        [Fact]
        public void Decode_StereoFile_Should_Throw()
        {
            var filePath = WriteMinimalCafFile(numChannels: 2, bitDepth: 16);
            try
            {
                var act = () => AlacDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_NonSixteenBitFile_Should_Throw()
        {
            var filePath = WriteMinimalCafFile(numChannels: 1, bitDepth: 24);
            try
            {
                var act = () => AlacDecoder.Decode(filePath, (_, _, _, _, _) => { });

                act.Should().ThrowExactly<NotSupportedException>();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_Should_Invoke_The_Callback_Once_Per_Packet_With_Consistent_Metadata()
        {
            var samples = new int[4096 * 3 + 100];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = i % 30;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_decoder_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 22050, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var callbackCount = 0;
                var totalDecodedFrames = 0L;

                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    callbackCount++;
                    channels.Should().Be(1);
                    sampleRate.Should().Be(22050);
                    bitsPerSample.Should().Be(16);
                    totalSamples.Should().Be(samples.Length);
                    totalDecodedFrames += block.Length;
                });

                callbackCount.Should().Be(4); // 3 full 4096-sample frames + 1 partial 100-sample frame
                totalDecodedFrames.Should().Be(samples.Length);
                streamInfo.TotalSamples.Should().Be(samples.Length);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static string WriteMinimalCafFile(int numChannels, int bitDepth)
        {
            var config = new AlacSpecificConfig
            {
                FrameLength = 4096,
                BitDepth = bitDepth,
                Pb = 40,
                Mb = 10,
                Kb = 14,
                NumChannels = numChannels,
                MaxRun = 255,
                SampleRate = 44100
            };

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_decoder_invalid_{Guid.NewGuid():N}.caf");
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            CafWriter.Write(stream, config, [], totalValidFrames: 0);

            return filePath;
        }
    }
}
