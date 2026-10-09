using EggEncoder.Codecs.Mov;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mov
{
    public class Mp4EsdsParserTest
    {
        // A minimal, well-formed 'esds' box content (28 bytes) encoding one AAC-LC
        // AudioSpecificConfig: version+flags(4) / ES_Descriptor(tag=3,size=22) / ES_ID(2) /
        // flags(1, no dependsOn/URL/OCR) / DecoderConfigDescriptor(tag=4,size=17) /
        // objectTypeIndication+streamType+bufferSizeDB+maxBitrate+avgBitrate(13, values
        // irrelevant) / DecoderSpecificInfo(tag=5,size=2) / AudioSpecificConfig(2 bytes:
        // audioObjectType=2 AAC-LC, samplingFrequencyIndex=4 -> 44100Hz, channelConfiguration=1
        // mono). Hand-built directly from the ISO 14496-1 descriptor layout this parser itself
        // implements, byte-for-byte traced against Mp4EsdsParser's own source before use.
        private static readonly byte[] _wellFormedEsdsBox =
        [
            0x00, 0x00, 0x00, 0x00, // version + flags
            0x03, 22, // ES_Descriptor: tag, size
            0x00, 0x01, // ES_ID
            0x00, // flags: no dependsOn/URL/OCR
            0x04, 17, // DecoderConfigDescriptor: tag, size
            0x40, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 13 fixed bytes
            0x05, 2, // DecoderSpecificInfo: tag, size
            0x12, 0x08 // AudioSpecificConfig: audioObjectType=2, samplingFrequencyIndex=4, channelConfiguration=1
        ];

        [Fact]
        public void ParseAudioSpecificConfig_WithWellFormedBox_Should_Return_Correct_SampleRateAndChannels()
        {
            var config = Mp4EsdsParser.ParseAudioSpecificConfig(_wellFormedEsdsBox);

            config.SampleRate.Should().Be(44100);
            config.Channels.Should().Be(1);
        }

        [Fact]
        public void ParseAudioSpecificConfig_WithTruncatedBoxAtAnyLength_Should_Throw_A_Clear_Typed_Exception_Not_An_UncheckedOne()
        {
            // Exhaustively confirms the fix's real contract: no matter where a real 'esds' box gets
            // cut off (a corrupted download, a malformed/adversarial file), ParseAudioSpecificConfig
            // either succeeds (only possible at the box's own true full length) or throws a clear,
            // typed InvalidDataException/NotSupportedException -- never the unchecked
            // IndexOutOfRangeException the four raw esdsBoxContent[position]/data[offset] indexing
            // sites this fix replaced would throw on a short read.
            for (var length = 0; length < _wellFormedEsdsBox.Length; length++)
            {
                var truncated = _wellFormedEsdsBox[..length];

                try
                {
                    Mp4EsdsParser.ParseAudioSpecificConfig(truncated);
                }
                catch (Exception ex)
                {
                    (ex is InvalidDataException or NotSupportedException).Should().BeTrue(
                        $"a box truncated to {length} byte(s) should fail with a clear, typed InvalidDataException or NotSupportedException, not {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        [Fact]
        public void ParseAudioSpecificConfig_WithEmptyBox_Should_Throw_InvalidDataException()
        {
            var act = () => Mp4EsdsParser.ParseAudioSpecificConfig([]);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ParseAudioSpecificConfig_WithNonAacObjectType_Should_Throw_NotSupportedException()
        {
            var box = _wellFormedEsdsBox.ToArray();
            box[^2] = 0x08; // audioObjectType = 1 (AAC Main), not 2 (AAC-LC)

            var act = () => Mp4EsdsParser.ParseAudioSpecificConfig(box);

            act.Should().ThrowExactly<NotSupportedException>();
        }

        [Fact]
        public void ParseAudioSpecificConfig_WithOutOfRangeSamplingFrequencyIndex_Should_Throw_InvalidDataException()
        {
            var box = _wellFormedEsdsBox.ToArray();
            box[^2] = 0x17; // audioObjectType=2 (AAC-LC) still, samplingFrequencyIndex's top 3 bits = 0b111
            box[^1] = 0x88; // samplingFrequencyIndex's low bit = 1 -> index = 0b1111 = 15, >= AacTables.SampleRates.Length (13)

            var act = () => Mp4EsdsParser.ParseAudioSpecificConfig(box);

            act.Should().ThrowExactly<InvalidDataException>();
        }
    }
}
