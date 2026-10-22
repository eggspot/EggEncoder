using EggEncoder.Codecs.Aac;
using EggEncoder.Transform;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacDecoderTest
    {
        private static readonly string _fixturePath = Path.GetFullPath("Codecs/Aac/tone_mono.aac");
        private static readonly string _groundTruthPath = Path.GetFullPath("Codecs/Aac/tone_mono_groundtruth.pcm");

        // -------- Stream-based Decode --------

        [Fact]
        public void Decode_Stream_Should_Round_Trip_Same_As_Path_Based_Decode()
        {
            var pathSamples = new List<int>();
            var pathStreamInfo = AacDecoder.Decode(_fixturePath, (block, _, _, _, _) => pathSamples.AddRange(block.ToArray()));

            var bytes = File.ReadAllBytes(_fixturePath);
            var streamSamples = new List<int>();
            using var memoryStream = new MemoryStream(bytes);
            var streamStreamInfo = AacDecoder.Decode(memoryStream, (block, _, _, _, _) => streamSamples.AddRange(block.ToArray()));

            streamStreamInfo.Channels.Should().Be(pathStreamInfo.Channels);
            streamStreamInfo.SampleRate.Should().Be(pathStreamInfo.SampleRate);
            streamSamples.Should().Equal(pathSamples);
        }

        [Fact]
        public void Decode_Stream_Should_NotDispose_TheCallersStream()
        {
            var bytes = File.ReadAllBytes(_fixturePath);
            var memoryStream = new MemoryStream(bytes);

            AacDecoder.Decode(memoryStream, (_, _, _, _, _) => { });

            var act = () => memoryStream.Position;
            act.Should().NotThrow();

            memoryStream.Dispose();
        }

        [Fact]
        public void Decode_Stream_WithNonSeekableStream_Should_StillWork()
        {
            var bytes = File.ReadAllBytes(_fixturePath);
            using var nonSeekable = new NonSeekableStream(bytes);

            var samples = new List<int>();
            var streamInfo = AacDecoder.Decode(nonSeekable, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            streamInfo.Channels.Should().Be(1);
            samples.Should().NotBeEmpty();
        }

        [Fact]
        public void Decode_Stream_WithNullStream_Should_Throw()
        {
            var act = () => AacDecoder.Decode((Stream)null!, (_, _, _, _, _) => { });
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Decode_ToneMono_Should_Return_Correct_Format()
        {
            var samples = new List<int>();
            var streamInfo = AacDecoder.Decode(_fixturePath, (block, _, _, _, _) => samples.AddRange(block.ToArray()));

            streamInfo.Channels.Should().Be(1);
            streamInfo.SampleRate.Should().Be(44100);
            samples.Should().NotBeEmpty();
        }

        [Fact]
        public void Decode_ToneMono_Should_MatchFfmpegGroundTruth_UpToScale()
        {
            var decodedSamples = new List<int>();
            AacDecoder.Decode(_fixturePath, (block, _, _, _, _) => decodedSamples.AddRange(block.ToArray()));

            var groundTruthBytes = File.ReadAllBytes(_groundTruthPath);
            var groundTruthSamples = new int[groundTruthBytes.Length / 2];
            for (var i = 0; i < groundTruthSamples.Length; i++)
            {
                groundTruthSamples[i] = (short)(groundTruthBytes[i * 2] | (groundTruthBytes[(i * 2) + 1] << 8));
            }

            var compareLength = Math.Min(decodedSamples.Count, groundTruthSamples.Length);
            compareLength.Should().BeGreaterThan(1000, "expected a meaningful number of comparable samples");

            double dotProduct = 0;
            double groundTruthEnergy = 0;
            for (var i = 0; i < compareLength; i++)
            {
                dotProduct += (double)decodedSamples[i] * groundTruthSamples[i];
                groundTruthEnergy += (double)groundTruthSamples[i] * groundTruthSamples[i];
            }

            groundTruthEnergy.Should().BeGreaterThan(0, "ground truth signal must not be silent");

            var scale = dotProduct / groundTruthEnergy;

            double errorEnergy = 0;
            double signalEnergy = 0;
            for (var i = 0; i < compareLength; i++)
            {
                var expected = groundTruthSamples[i] * scale;
                var error = decodedSamples[i] - expected;
                errorEnergy += error * error;
                signalEnergy += expected * expected;
            }

            var signalToNoiseRatioDb = 10 * Math.Log10(signalEnergy / Math.Max(errorEnergy, 1e-9));

            signalToNoiseRatioDb.Should().BeGreaterThan(20, $"expected SNR > 20dB against ground truth (scale={scale}), got {signalToNoiseRatioDb}dB");
        }

        [Fact]
        public void Decode_WithMissingFile_Should_Throw()
        {
            var act = () => AacDecoder.Decode(Path.GetFullPath("Codecs/Aac/does_not_exist.aac"), (_, _, _, _, _) => { });
            act.Should().ThrowExactly<FileNotFoundException>();
        }

        // -------- Malformed input (found by fuzzing Probe(Stream)) --------

        [Fact]
        public void Decode_Stream_WithFrameLengthShorterThanHeader_Should_ThrowInvalidDataException()
        {
            // Before the check, skipping to a frame end inside the header threw a raw
            // ArgumentOutOfRangeException ("Cannot skip backwards") from BitReader.
            var bytes = FirstFrameDeclaring(_ => 3);
            using var stream = new MemoryStream(bytes);

            var act = () => AacDecoder.Decode(stream, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*frame_length 3*");
        }

        [Fact]
        public void Decode_Stream_WithFrameLengthShorterThanRawDataBlock_Should_ThrowInvalidDataException()
        {
            // The real frame is 321 bytes; declaring 318 leaves 3 trailing bytes, too few for another
            // header, so counting passes and the decode of the frame itself overruns its declared end.
            var bytes = FirstFrameDeclaring(real => real - 3);
            using var stream = new MemoryStream(bytes);

            var act = () => AacDecoder.Decode(stream, (_, _, _, _, _) => { });

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*runs past the ADTS frame_length*");
        }

        [Fact]
        public void Decode_Stream_WithUnmodifiedFirstFrame_Should_DecodeOneFrame()
        {
            var bytes = FirstFrameDeclaring(real => real);
            using var stream = new MemoryStream(bytes);
            var blocks = 0;

            var info = AacDecoder.Decode(stream, (_, _, _, _, _) => blocks++);

            blocks.Should().Be(1);
            info.TotalSamples.Should().Be(1024);
        }

        [Fact]
        public void DecodeFrame_WithSectionRunningPastMaxSfb_Should_ThrowInvalidDataException()
        {
            // A long-window single_channel_element whose only section (band type 1) claims 2 bands
            // while max_sfb is 1. Before the check this wrote past the band-type array
            // (IndexOutOfRangeException).
            var writer = new BitWriter();
            writer.WriteBits(0, 3);   // id_syn_ele: SCE
            writer.WriteBits(0, 4);   // element_instance_tag
            writer.WriteBits(100, 8); // global_gain
            writer.WriteBits(0, 1);   // ics_reserved_bit
            writer.WriteBits(0, 2);   // window_sequence: ONLY_LONG
            writer.WriteBits(0, 1);   // window_shape
            writer.WriteBits(1, 6);   // max_sfb
            writer.WriteBits(0, 1);   // predictor_data_present
            writer.WriteBits(1, 4);   // sect_cb
            writer.WriteBits(2, 5);   // sect_len_incr
            writer.WriteBits(0, 32);  // padding so the read can't stop at end of data first
            var reader = new BitReader(writer.ToArray());

            var act = () => new AacFrameDecoder().DecodeFrame(reader, channels: 1);

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*section data runs past max_sfb*");
        }

        // The fixture's first ADTS frame alone (321 bytes, no CRC), with its frame_length field
        // rewritten to the given value.
        private static byte[] FirstFrameDeclaring(Func<int, int> declaredLength)
        {
            var fixture = File.ReadAllBytes(_fixturePath);
            var realLength = ((fixture[3] & 0x3) << 11) | (fixture[4] << 3) | (fixture[5] >> 5);
            var frame = fixture.AsSpan(0, realLength).ToArray();
            var declared = declaredLength(realLength);
            frame[3] = (byte)((frame[3] & 0xFC) | ((declared >> 11) & 0x3));
            frame[4] = (byte)(declared >> 3);
            frame[5] = (byte)((frame[5] & 0x1F) | ((declared & 0x7) << 5));
            return frame;
        }
    }
}
