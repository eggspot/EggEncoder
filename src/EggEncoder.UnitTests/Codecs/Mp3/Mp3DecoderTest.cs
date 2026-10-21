using EggEncoder.Codecs.Mp3;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3DecoderTest
    {
        [Fact]
        public void Decode_ToneMp3_Should_Produce_Correct_Format_And_NonSilent_Samples()
        {
            var (streamInfo, samples) = Mp3TestDecoder.DecodeAll(Path.GetFullPath("Codecs/Mp3/tone.mp3"));

            streamInfo.Channels.Should().Be(2);
            streamInfo.SampleRate.Should().Be(44100);
            streamInfo.BitsPerSample.Should().Be(16);
            samples.Should().NotBeEmpty();

            var rootMeanSquare = Math.Sqrt(samples.Average(sample => (double)sample * sample));
            rootMeanSquare.Should().BeGreaterThan(1000, $"expected a real, non-silent decoded signal, got RMS={rootMeanSquare}");
        }

        [Fact]
        public void Decode_Stream_Should_Produce_Same_Samples_And_StreamInfo_As_PathBased()
        {
            var fixturePath = Path.GetFullPath("Codecs/Mp3/tone.mp3");
            var (pathInfo, pathSamples) = Mp3TestDecoder.DecodeAll(fixturePath);

            using var stream = File.OpenRead(fixturePath);
            var (streamInfo, streamSamples) = DecodeAll(stream);

            streamInfo.Should().BeEquivalentTo(pathInfo);
            streamSamples.Should().Equal(pathSamples);
        }

        [Fact]
        public void Decode_NonSeekableStream_Should_Produce_Same_Samples_As_PathBased()
        {
            var fixturePath = Path.GetFullPath("Codecs/Mp3/tone.mp3");
            var (pathInfo, pathSamples) = Mp3TestDecoder.DecodeAll(fixturePath);

            using var stream = new NonSeekableStream(File.ReadAllBytes(fixturePath));
            var (streamInfo, streamSamples) = DecodeAll(stream);

            streamInfo.Should().BeEquivalentTo(pathInfo);
            streamSamples.Should().Equal(pathSamples);
        }

        [Fact]
        public void Decode_Stream_Should_Not_Dispose_The_Callers_Stream()
        {
            // NLayer's own MpegFile(Stream) always disposes the stream it's handed -- this guards
            // that it only ever sees Mp3Decoder's own buffer, never the caller's stream.
            using var stream = new MemoryStream(File.ReadAllBytes(Path.GetFullPath("Codecs/Mp3/tone.mp3")));

            DecodeAll(stream);

            stream.CanRead.Should().BeTrue();
        }

        [Fact]
        public void Decode_Stream_Should_Start_From_The_Streams_Current_Position()
        {
            var fixturePath = Path.GetFullPath("Codecs/Mp3/tone.mp3");
            var (_, pathSamples) = Mp3TestDecoder.DecodeAll(fixturePath);

            // A leading run of unrelated bytes the caller has already consumed (e.g. a container
            // header of its own) -- decode must treat the current position as the file's start.
            var prefix = new byte[333];
            Array.Fill(prefix, (byte)0xAB);
            using var stream = new MemoryStream([.. prefix, .. File.ReadAllBytes(fixturePath)]);
            stream.Position = prefix.Length;

            var (_, streamSamples) = DecodeAll(stream);

            streamSamples.Should().Equal(pathSamples);
        }

        [Fact]
        public void Decode_NullStream_Should_Throw_ArgumentNullException()
        {
            var act = () => Mp3Decoder.Decode((Stream)null!, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<ArgumentNullException>();
        }

        private static (Mp3StreamInfo StreamInfo, int[] InterleavedSamples) DecodeAll(Stream stream)
        {
            var samples = new List<int>();
            var streamInfo = Mp3Decoder.Decode(stream, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            return (streamInfo, [.. samples]);
        }
    }
}
