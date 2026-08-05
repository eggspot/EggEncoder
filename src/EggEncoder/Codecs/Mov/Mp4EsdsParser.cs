using EggEncoder.Codecs.Aac;

namespace EggEncoder.Codecs.Mov
{
    // Parses the ISO 14496-1 descriptor tree inside an 'esds' box down to the AAC
    // AudioSpecificConfig, which carries the sample rate and channel count for an 'mp4a' track.
    // Only what's needed to read those two fields is implemented -- SBR/PS extension configs,
    // and every other MPEG-4 audio object type besides plain AAC-LC, are out of scope.
    internal static class Mp4EsdsParser
    {
        private const int EsDescriptorTag = 0x03;
        private const int DecoderConfigDescriptorTag = 0x04;
        private const int DecoderSpecificInfoTag = 0x05;
        private const int AacLowComplexityObjectType = 2;

        public static Mp4AudioConfig ParseAudioSpecificConfig(byte[] esdsBoxContent)
        {
            // esdsBoxContent starts with the box's own version(1) + flags(3).
            var position = 4;

            if (!TryReadDescriptorHeader(esdsBoxContent, ref position, out var tag, out var size) || tag != EsDescriptorTag)
            {
                throw new InvalidDataException("'esds' box does not start with an ES_Descriptor");
            }

            var esDescriptorEnd = position + size;

            position += 2; // ES_ID
            var flags = esdsBoxContent[position];
            position += 1;

            if ((flags & 0x80) != 0)
            {
                position += 2; // dependsOn_ES_ID
            }

            if ((flags & 0x40) != 0)
            {
                var urlLength = esdsBoxContent[position];
                position += 1 + urlLength;
            }

            if ((flags & 0x20) != 0)
            {
                position += 2; // OCR_ES_Id
            }

            while (position < esDescriptorEnd)
            {
                if (!TryReadDescriptorHeader(esdsBoxContent, ref position, out var innerTag, out var innerSize))
                {
                    break;
                }

                var innerEnd = position + innerSize;

                if (innerTag == DecoderConfigDescriptorTag)
                {
                    var config = ParseDecoderConfigDescriptor(esdsBoxContent, position, innerEnd);
                    if (config is not null)
                    {
                        return config;
                    }
                }

                position = innerEnd;
            }

            throw new InvalidDataException("'esds' box does not contain a DecoderSpecificInfo with an AudioSpecificConfig");
        }

        private static Mp4AudioConfig? ParseDecoderConfigDescriptor(byte[] data, int start, int end)
        {
            // objectTypeIndication(1) + streamType/upStream/reserved(1) + bufferSizeDB(3) +
            // maxBitrate(4) + avgBitrate(4) = 13 fixed bytes before any nested descriptors.
            var position = start + 13;

            while (position < end)
            {
                if (!TryReadDescriptorHeader(data, ref position, out var tag, out var size))
                {
                    break;
                }

                if (tag == DecoderSpecificInfoTag && size >= 2)
                {
                    return ParseAudioSpecificConfigBytes(data, position);
                }

                position += size;
            }

            return null;
        }

        private static Mp4AudioConfig ParseAudioSpecificConfigBytes(byte[] data, int offset)
        {
            var b0 = data[offset];
            var b1 = data[offset + 1];

            var audioObjectType = (b0 >> 3) & 0x1F;
            var samplingFrequencyIndex = ((b0 & 0x07) << 1) | (b1 >> 7);
            var channelConfiguration = (b1 >> 3) & 0x0F;

            if (audioObjectType != AacLowComplexityObjectType)
            {
                throw new NotSupportedException($"MPEG-4 audio object type {audioObjectType} is not supported; only AAC-LC (2) is supported");
            }

            if (samplingFrequencyIndex >= AacTables.SampleRates.Length)
            {
                throw new InvalidDataException($"Invalid AudioSpecificConfig sampling_frequency_index {samplingFrequencyIndex}");
            }

            return new Mp4AudioConfig
            {
                SampleRate = AacTables.SampleRates[samplingFrequencyIndex],
                Channels = channelConfiguration
            };
        }

        private static bool TryReadDescriptorHeader(byte[] data, ref int position, out int tag, out int size)
        {
            tag = 0;
            size = 0;

            if (position >= data.Length)
            {
                return false;
            }

            tag = data[position++];

            for (var i = 0; i < 4; i++)
            {
                if (position >= data.Length)
                {
                    return false;
                }

                var b = data[position++];
                size = (size << 7) | (b & 0x7F);

                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            return true;
        }
    }

    internal sealed class Mp4AudioConfig
    {
        public required int SampleRate { get; init; }

        public required int Channels { get; init; }
    }
}
