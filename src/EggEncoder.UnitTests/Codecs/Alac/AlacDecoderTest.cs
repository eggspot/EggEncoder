using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Wav;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class AlacDecoderTest
    {
        // -------- Stream-based Decode --------

        [Fact]
        public void Decode_Stream_Should_Round_Trip_Same_As_Path_Based_Decode()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var cafPath = Path.Combine(tempDirectory, "dest.caf");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1000, -1000, 500]);
                AlacEncoder.Encode(wavPath, cafPath);

                var pathSamples = new List<int>();
                var pathStreamInfo = AlacDecoder.Decode(cafPath, (block, _, _, _, _) => pathSamples.AddRange(block.ToArray()));

                var bytes = File.ReadAllBytes(cafPath);
                var streamSamples = new List<int>();
                using var memoryStream = new MemoryStream(bytes);
                var streamStreamInfo = AlacDecoder.Decode(memoryStream, (block, _, _, _, _) => streamSamples.AddRange(block.ToArray()));

                streamStreamInfo.Channels.Should().Be(pathStreamInfo.Channels);
                streamStreamInfo.SampleRate.Should().Be(pathStreamInfo.SampleRate);
                streamSamples.Should().Equal(pathSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Decode_Stream_Should_NotDispose_TheCallersStream()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var cafPath = Path.Combine(tempDirectory, "dest.caf");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1]);
                AlacEncoder.Encode(wavPath, cafPath);

                var memoryStream = new MemoryStream(File.ReadAllBytes(cafPath));
                AlacDecoder.Decode(memoryStream, (_, _, _, _, _) => { });

                var act = () => memoryStream.Position;
                act.Should().NotThrow();

                memoryStream.Dispose();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Decode_Stream_WithNullStream_Should_Throw()
        {
            var act = () => AlacDecoder.Decode((Stream)null!, (_, _, _, _, _) => { });
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Decode_Stream_WithNonSeekableStream_Should_Throw()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var cafPath = Path.Combine(tempDirectory, "dest.caf");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1]);
                AlacEncoder.Encode(wavPath, cafPath);

                using var nonSeekable = new NonSeekableStream(File.ReadAllBytes(cafPath));
                var act = () => AlacDecoder.Decode(nonSeekable, (_, _, _, _, _) => { });

                act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("stream");
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Decode_UnsupportedChannelCountFile_Should_Throw()
        {
            var filePath = WriteMinimalCafFile(numChannels: 3, bitDepth: 16);
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
        public void Decode_StereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var interleaved = new[] { 100, -200, 300, -400, 500, -600 };

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_decoder_stereo_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(interleaved, frameCount: 3);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    channels.Should().Be(2);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                streamInfo.TotalSamples.Should().Be(3);
                decoded.Should().Equal(interleaved);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Decode_UnsupportedBitDepthFile_Should_Throw()
        {
            var filePath = WriteMinimalCafFile(numChannels: 1, bitDepth: 20);
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
        public void Decode_TwentyFourBitStereoFile_Should_Invoke_The_Callback_With_InterleavedSamples()
        {
            var interleaved = new[] { 1_000_000, -2_000_000, 3_000_000, -4_000_000, 8_388_607, -8_388_608 };

            var filePath = Path.Combine(Path.GetTempPath(), $"alac_decoder_24bit_stereo_{Guid.NewGuid():N}.caf");
            try
            {
                using (var session = AlacEncoderSession.OpenSession(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 24))
                {
                    session.WriteInterleavedSamples(interleaved, frameCount: 3);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = AlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    channels.Should().Be(2);
                    bitsPerSample.Should().Be(24);
                    decoded.AddRange(block.ToArray());
                });

                streamInfo.Channels.Should().Be(2);
                streamInfo.BitsPerSample.Should().Be(24);
                streamInfo.TotalSamples.Should().Be(3);
                decoded.Should().Equal(interleaved);
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
