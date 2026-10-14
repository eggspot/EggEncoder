using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Wav;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaDecoderTest
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
                var ttaPath = Path.Combine(tempDirectory, "dest.tta");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1000, -1000, 500]);
                TtaEncoder.Encode(wavPath, ttaPath);

                var pathSamples = new List<int>();
                var pathStreamInfo = TtaDecoder.Decode(ttaPath, (block, _, _, _, _) => pathSamples.AddRange(block.ToArray()));

                var bytes = File.ReadAllBytes(ttaPath);
                var streamSamples = new List<int>();
                using var memoryStream = new MemoryStream(bytes);
                var streamStreamInfo = TtaDecoder.Decode(memoryStream, (block, _, _, _, _) => streamSamples.AddRange(block.ToArray()));

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
                var ttaPath = Path.Combine(tempDirectory, "dest.tta");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1]);
                TtaEncoder.Encode(wavPath, ttaPath);

                var memoryStream = new MemoryStream(File.ReadAllBytes(ttaPath));
                TtaDecoder.Decode(memoryStream, (_, _, _, _, _) => { });

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
        public void Decode_Stream_WithNonSeekableStream_Should_StillWork()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var wavPath = Path.Combine(tempDirectory, "source.wav");
                var ttaPath = Path.Combine(tempDirectory, "dest.tta");
                WavFileBuilder.Create(wavPath, channels: 1, sampleRate: 8000, bitsPerSample: 16, [0, 1000, -1000, 500]);
                TtaEncoder.Encode(wavPath, ttaPath);

                using var nonSeekable = new NonSeekableStream(File.ReadAllBytes(ttaPath));
                var samples = new List<int>();
                var streamInfo = TtaDecoder.Decode(nonSeekable, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

                streamInfo.Channels.Should().Be(1);
                samples.Should().NotBeEmpty();
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void Decode_Stream_WithNullStream_Should_Throw()
        {
            var act = () => TtaDecoder.Decode((Stream)null!, (_, _, _, _, _) => { });
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Decode_UnsupportedChannelCountFile_Should_Throw()
        {
            var filePath = WriteMinimalTtaFile(channels: 3, bitsPerSample: 16);
            try
            {
                var act = () => TtaDecoder.Decode(filePath, (_, _, _, _, _) => { });

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
            var filePath = WriteMinimalTtaFile(channels: 1, bitsPerSample: 24);
            try
            {
                var act = () => TtaDecoder.Decode(filePath, (_, _, _, _, _) => { });

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

            var filePath = Path.Combine(Path.GetTempPath(), $"tta_decoder_stereo_{Guid.NewGuid():N}.tta");
            try
            {
                using (var session = TtaEncoderSession.OpenSession(filePath, channels: 2, sampleRate: 44100, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(interleaved, frameCount: 3);
                    session.Finish();
                }

                var decoded = new List<int>();
                var streamInfo = TtaDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
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
        public void Decode_Should_Invoke_The_Callback_Once_Per_Frame_With_Consistent_Metadata()
        {
            // At sampleRate=245, the frame length is exactly 256 -- 700 samples spans 3 frames (256,
            // 256, 188), mirroring ALAC's equivalent multi-packet callback-count test.
            var samples = new int[700];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (i % 30) - 15;
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"tta_decoder_{Guid.NewGuid():N}.tta");
            try
            {
                using (var session = TtaEncoderSession.OpenSession(filePath, channels: 1, sampleRate: 245, bitsPerSample: 16))
                {
                    session.WriteInterleavedSamples(samples, samples.Length);
                    session.Finish();
                }

                var callbackCount = 0;
                var totalDecodedFrames = 0L;

                var streamInfo = TtaDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    callbackCount++;
                    channels.Should().Be(1);
                    sampleRate.Should().Be(245);
                    bitsPerSample.Should().Be(16);
                    totalSamples.Should().Be(samples.Length);
                    totalDecodedFrames += block.Length;
                });

                callbackCount.Should().Be(3);
                totalDecodedFrames.Should().Be(samples.Length);
                streamInfo.TotalSamples.Should().Be(samples.Length);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        private static string WriteMinimalTtaFile(int channels, int bitsPerSample)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_decoder_invalid_{Guid.NewGuid():N}.tta");
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            TtaWriter.Write(stream, channels, bitsPerSample, sampleRate: 245, totalSamples: 0, []);

            return filePath;
        }
    }
}
