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
            var flags = ReadByte(esdsBoxContent, position);
            position += 1;

            if ((flags & 0x80) != 0)
            {
                position += 2; // dependsOn_ES_ID
            }

            if ((flags & 0x40) != 0)
            {
                var urlLength = ReadByte(esdsBoxContent, position);
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
            // ParseDecoderConfigDescriptor's own `size >= 2` check only validates the descriptor's
            // own DECLARED size field -- it doesn't guarantee the backing array actually has 2 real
            // bytes at this offset for a truncated/corrupted file, so this still needs its own check.
            var b0 = ReadByte(data, offset);
            var b1 = ReadByte(data, offset + 1);

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

        // Every raw byte read in this file outside TryReadDescriptorHeader's own already-guarded
        // loop funnels through here -- confirmed by a truncated/corrupted 'esds' box previously
        // throwing an unchecked IndexOutOfRangeException instead of the clear, typed
        // InvalidDataException every other container reader in this codebase gives for the same
        // class of malformed input (structurally the same bug AsfContainerReader had on truncated
        // WMA files, just here in MOV/MP4's own AAC audio-config parsing).
        private static byte ReadByte(byte[] data, int position)
        {
            if (position < 0 || position >= data.Length)
            {
                throw new InvalidDataException($"'esds' box is truncated or corrupted: expected a byte at position {position}, but the box is only {data.Length} byte(s) long");
            }

            return data[position];
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

        // Write side -- the inverse of ParseAudioSpecificConfigBytes/ParseAudioSpecificConfig above,
        // used by Streaming.Mp4.FmpInitSegmentWriter to build an 'esds' box for a fragmented MP4/CMAF
        // init segment. Confirmed against a real ffmpeg-produced fMP4 fixture's own esds bytes (same
        // field values: objectTypeIndication 0x40, streamType/upStream/reserved 0x15, an SLConfigDescriptor
        // with predefined=2) -- this writer uses the minimal single-byte descriptor-length encoding
        // throughout rather than ffmpeg's own always-4-byte-padded one, since TryReadDescriptorHeader
        // above already reads either form correctly and the minimal form is simpler to write.
        public static byte[] BuildAudioSpecificConfig(int channels, int sampleRate)
        {
            var sampleRateIndex = Array.IndexOf(AacTables.SampleRates, sampleRate);
            if (sampleRateIndex < 0)
            {
                throw new NotSupportedException($"Sample rate {sampleRate} is not a valid MPEG-4 AAC sample rate");
            }

            // audioObjectType(5)=2 (AAC-LC) + samplingFrequencyIndex(4) + channelConfiguration(4) +
            // frameLengthFlag(1)=0 + dependsOnCoreCoder(1)=0 + extensionFlag(1)=0 = 16 bits exactly.
            var b0 = (byte)((AacLowComplexityObjectType << 3) | (sampleRateIndex >> 1));
            var b1 = (byte)(((sampleRateIndex & 0x01) << 7) | (channels << 3));

            return [b0, b1];
        }

        public static byte[] BuildEsdsBoxContent(byte[] audioSpecificConfig)
        {
            const byte esDescriptorTag = EsDescriptorTag;
            const byte decoderConfigDescriptorTag = DecoderConfigDescriptorTag;
            const byte decoderSpecificInfoTag = DecoderSpecificInfoTag;
            const byte slConfigDescriptorTag = 0x06;
            const byte mpeg4AudioObjectTypeIndication = 0x40;
            const byte audioStreamTypeUpstreamReserved = 0x15; // streamType=5 (AudioStream), upStream=0, reserved=1
            const byte slConfigPredefinedNull = 0x02; // "reserved for use in MP4 files" / no SL packet header

            var decoderConfigDescriptor = new Mp4BoxBuilder()
                .WriteUInt8(mpeg4AudioObjectTypeIndication)
                .WriteUInt8(audioStreamTypeUpstreamReserved)
                .WriteUInt8(0).WriteUInt8(0).WriteUInt8(0) // bufferSizeDB(3) -- unknown/unspecified
                .WriteUInt32(0) // maxBitrate -- unknown/unspecified
                .WriteUInt32(0) // avgBitrate -- unknown/unspecified
                .WriteUInt8(decoderSpecificInfoTag)
                .WriteUInt8((byte)audioSpecificConfig.Length)
                .WriteBytes(audioSpecificConfig)
                .ToArray();

            var esDescriptorContent = new Mp4BoxBuilder()
                .WriteUInt16(0) // ES_ID
                .WriteUInt8(0) // flags: no dependsOn/URL/OCR
                .WriteUInt8(decoderConfigDescriptorTag)
                .WriteUInt8((byte)decoderConfigDescriptor.Length)
                .WriteBytes(decoderConfigDescriptor)
                .WriteUInt8(slConfigDescriptorTag)
                .WriteUInt8(1)
                .WriteUInt8(slConfigPredefinedNull)
                .ToArray();

            return new Mp4BoxBuilder()
                .WriteUInt32(0) // version(1) + flags(3)
                .WriteUInt8(esDescriptorTag)
                .WriteUInt8((byte)esDescriptorContent.Length)
                .WriteBytes(esDescriptorContent)
                .ToArray();
        }
    }

    internal sealed class Mp4AudioConfig
    {
        public required int SampleRate { get; init; }

        public required int Channels { get; init; }
    }
}
