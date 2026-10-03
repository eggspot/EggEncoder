using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Opus
{
    // Builds and parses the two mandatory header packets every OggOpus stream starts with
    // (RFC 7845): 'OpusHead' (channel count, pre-skip, original sample rate) and 'OpusTags' (a
    // vendor string plus zero or more comments -- this always writes zero comments, and ignores
    // whatever a file it reads actually contains, since EggEncoder has no metadata/tagging surface
    // for any other format either). Channel mapping family is always written as 0 (mono/stereo,
    // the "simple" mapping with no extra channel mapping table) -- anything else is out of scope,
    // matching every other codec's mono/stereo-only convention here.
    internal static class OpusHeaderPackets
    {
        private const string VendorString = "EggEncoder";

        public static byte[] BuildOpusHead(int channels, int preSkip, int inputSampleRate)
        {
            var packet = new byte[19];
            "OpusHead"u8.CopyTo(packet);
            packet[8] = 1; // version
            packet[9] = (byte)channels;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (ushort)preSkip);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)inputSampleRate);
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(16, 2), 0); // output gain
            packet[18] = 0; // channel mapping family

            return packet;
        }

        public static OpusHead ParseOpusHead(byte[] packet, string filePath)
        {
            if (packet.Length < 19 || Encoding.ASCII.GetString(packet, 0, 8) != "OpusHead")
            {
                throw new InvalidDataException($"'{filePath}' is not a valid OggOpus file: missing 'OpusHead' packet");
            }

            var version = packet[8];
            if (version != 1)
            {
                throw new NotSupportedException($"'{filePath}' uses OpusHead version {version}; only version 1 is supported");
            }

            var channels = packet[9];
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' has {channels} channels; only mono and stereo Opus are supported");
            }

            var channelMappingFamily = packet[18];
            if (channelMappingFamily != 0)
            {
                throw new NotSupportedException($"'{filePath}' uses channel mapping family {channelMappingFamily}; only family 0 (mono/stereo) is supported");
            }

            return new OpusHead
            {
                Channels = channels,
                PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10, 2)),
                InputSampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4))
            };
        }

        public static byte[] BuildOpusTags()
        {
            var vendorBytes = Encoding.UTF8.GetBytes(VendorString);
            var packet = new byte[8 + 4 + vendorBytes.Length + 4];

            "OpusTags"u8.CopyTo(packet);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)vendorBytes.Length);
            vendorBytes.CopyTo(packet, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12 + vendorBytes.Length, 4), 0); // user comment list length

            return packet;
        }

        public static void ValidateOpusTags(byte[] packet, string filePath)
        {
            if (packet.Length < 8 || Encoding.ASCII.GetString(packet, 0, 8) != "OpusTags")
            {
                throw new InvalidDataException($"'{filePath}' is not a valid OggOpus file: missing 'OpusTags' packet");
            }
        }
    }

    internal sealed class OpusHead
    {
        public required int Channels { get; init; }

        public required int PreSkip { get; init; }

        public required int InputSampleRate { get; init; }
    }
}
