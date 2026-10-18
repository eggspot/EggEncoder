using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mp3
{
    // One fully-decoded MPEG-1 Layer III frame header -- every field Mp3SideInfoParser/the rest of
    // the decode chain needs, unlike Mp3Probe's own lighter-weight Mp3FrameHeader (which omits the
    // raw index/mode fields a full decode, not just duration/metadata probing, requires).
    internal readonly record struct Mp3DecodedFrameHeader(
        int SampleRate,
        int BitRateKbps,
        int Channels,
        int FrameLengthBytes,
        int SideInfoSize,
        bool HasCrc,
        int ModeExtension);

    // Parses the 32-bit MPEG-1 Layer III frame header Mp3FrameEncoder.WriteHeader writes, for the
    // in-house decode chain (docs/external-dependency-removal-plan.md Phase 1 sub-item 1a).
    // Reuses Mp3Probe's own bitrate/sample-rate tables (made internal specifically so this type and
    // Mp3FrameEncoder can both read them, rather than each re-tabulating the same ISO values) so
    // encode and decode can never silently disagree about what a given index means. Scoped to
    // MPEG-1 only and independent (non-joint) stereo only, matching sub-item 1a's own stated scope
    // -- MPEG-2/2.5 (LSF) is sub-item 1d, joint stereo is 1b. Throws a distinct, specific
    // NotSupportedException for anything outside that scope (wrong version/layer, joint stereo,
    // free-format/reserved bitrate, reserved sample rate) rather than silently mis-decoding, per
    // that sub-item's own acceptance bar.
    internal static class Mp3FrameHeaderParser
    {
        private const uint SyncWord = 0x7FF;
        private const uint Mpeg1VersionBits = 0b11;
        private const uint Layer3Bits = 0b01;
        private const uint JointStereoModeBits = 0b01;
        private const uint MonoModeBits = 0b11;

        // reader must be positioned at the start of the 32-bit header (i.e. right after any ID3v2
        // tag/preceding frame, at a real sync word) -- this method does not itself search for sync.
        public static Mp3DecodedFrameHeader Parse(BitReader reader)
        {
            var syncWord = reader.ReadBits(11);
            if (syncWord != SyncWord)
            {
                throw new InvalidDataException($"Expected an MP3 frame sync word (0x7FF) but found 0x{syncWord:X3}");
            }

            var versionBits = reader.ReadBits(2);
            if (versionBits != Mpeg1VersionBits)
            {
                throw new NotSupportedException($"Only MPEG-1 is supported for MP3 decode, not version bits {versionBits}");
            }

            var layerBits = reader.ReadBits(2);
            if (layerBits != Layer3Bits)
            {
                throw new NotSupportedException($"Only Layer III is supported for MP3 decode, not layer bits {layerBits}");
            }

            var protectionBit = reader.ReadBits(1);
            var hasCrc = protectionBit == 0;

            var bitrateIndex = (int)reader.ReadBits(4);
            var sampleRateIndex = (int)reader.ReadBits(2);
            var paddingBit = (int)reader.ReadBits(1);
            reader.ReadBits(1); // private_bit -- decorative, not used by decode

            var channelModeBits = reader.ReadBits(2);
            var modeExtensionBits = (int)reader.ReadBits(2);
            reader.ReadBits(1); // copyright -- decorative, not used by decode
            reader.ReadBits(1); // original -- decorative, not used by decode
            reader.ReadBits(2); // emphasis -- decorative, not used by decode

            if (channelModeBits == JointStereoModeBits)
            {
                throw new NotSupportedException("Joint stereo (MS/intensity) frames are not yet supported for MP3 decode -- only independent stereo, dual channel, and mono.");
            }

            var channels = channelModeBits == MonoModeBits ? 1 : 2;

            if (bitrateIndex is 0 or 15)
            {
                throw new NotSupportedException($"Free-format or reserved bitrate index ({bitrateIndex}) is not supported for MP3 decode");
            }

            if (Mp3Probe.Mpeg1SampleRates[sampleRateIndex] < 0)
            {
                throw new NotSupportedException($"Reserved sample rate index ({sampleRateIndex}) is not supported for MP3 decode");
            }

            var bitRateKbps = Mp3Probe.Mpeg1Layer3BitrateKbps[bitrateIndex];
            var sampleRate = Mp3Probe.Mpeg1SampleRates[sampleRateIndex];
            var frameLengthBytes = (144 * bitRateKbps * 1000 / sampleRate) + paddingBit;
            var sideInfoSize = channels == 1 ? 17 : 32;

            return new Mp3DecodedFrameHeader(sampleRate, bitRateKbps, channels, frameLengthBytes, sideInfoSize, hasCrc, modeExtensionBits);
        }
    }
}
