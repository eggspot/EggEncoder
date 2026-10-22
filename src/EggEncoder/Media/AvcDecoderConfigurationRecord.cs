namespace EggEncoder.Media
{
    /// <summary>
    /// An H.264 <c>AVCDecoderConfigurationRecord</c> (ISO/IEC 14496-15 clause 5.3.3.1): the codec
    /// configuration FLV/RTMP sequence headers, MP4 <c>avcC</c> boxes and
    /// <see cref="MediaTrackInfo.CodecConfiguration"/> all carry for H.264. <see cref="Parse"/> and
    /// <see cref="ToArray"/> are exact inverses for any well-formed record.
    /// </summary>
    /// <param name="ProfileIndication">AVCProfileIndication (the SPS's profile_idc).</param>
    /// <param name="ProfileCompatibility">profile_compatibility (the SPS's constraint flags byte).</param>
    /// <param name="LevelIndication">AVCLevelIndication (the SPS's level_idc).</param>
    /// <param name="NalLengthSize">Bytes per NAL length prefix in this track's packets: 1, 2 or 4.</param>
    /// <param name="SequenceParameterSets">SPS NAL units, each with its NAL header byte.</param>
    /// <param name="PictureParameterSets">PPS NAL units, each with its NAL header byte.</param>
    /// <param name="HighProfileExtension">
    /// The chroma format / bit depth / SPS-extension fields the record carries for every profile other
    /// than Baseline, Main and Extended (66, 77, 88), as current editions of ISO/IEC 14496-15 specify
    /// (the first edition listed only 100, 110, 122 and 144); <c>null</c> when the record ends after
    /// its PPS list, which encoders predating that extension still write.
    /// </param>
    /// <remarks>
    /// Record equality compares the NAL unit lists by reference, not by contents; compare
    /// <see cref="ToArray"/> output to test two records for byte equality.
    /// </remarks>
    public sealed record AvcDecoderConfigurationRecord(
        int ProfileIndication,
        int ProfileCompatibility,
        int LevelIndication,
        int NalLengthSize,
        IReadOnlyList<ReadOnlyMemory<byte>> SequenceParameterSets,
        IReadOnlyList<ReadOnlyMemory<byte>> PictureParameterSets,
        AvcHighProfileExtension? HighProfileExtension = null)
    {
        private const string What = "AVCDecoderConfigurationRecord";

        /// <summary>Parses a record. Every NAL unit is copied, so the result doesn't alias <paramref name="data"/>.</summary>
        /// <exception cref="InvalidDataException">Truncated, an unknown version, no SPS, or a NAL unit length past the end.</exception>
        public static AvcDecoderConfigurationRecord Parse(ReadOnlySpan<byte> data)
        {
            var reader = new ConfigurationRecordReader(data, What);
            var version = reader.ReadByte();
            if (version != 1)
            {
                throw new InvalidDataException($"{What}: unsupported configurationVersion {version}");
            }

            var profile = reader.ReadByte();
            var compatibility = reader.ReadByte();
            var level = reader.ReadByte();
            var nalLengthSize = (reader.ReadByte() & 0x03) + 1;
            if (nalLengthSize == 3)
            {
                throw new InvalidDataException($"{What}: lengthSizeMinusOne of 2 is not allowed");
            }

            var spsCount = reader.ReadByte() & 0x1F;
            if (spsCount == 0)
            {
                throw new InvalidDataException($"{What}: no sequence parameter set");
            }

            var sps = reader.ReadNalUnits(spsCount);
            var pps = reader.ReadNalUnits(reader.ReadByte());

            AvcHighProfileExtension? extension = null;
            if (!reader.AtEnd && profile is not (66 or 77 or 88))
            {
                var chromaFormat = reader.ReadByte() & 0x03;
                var bitDepthLuma = (reader.ReadByte() & 0x07) + 8;
                var bitDepthChroma = (reader.ReadByte() & 0x07) + 8;
                var spsExtensions = reader.ReadNalUnits(reader.ReadByte());
                extension = new AvcHighProfileExtension(chromaFormat, bitDepthLuma, bitDepthChroma, spsExtensions);
            }

            return new AvcDecoderConfigurationRecord(profile, compatibility, level, nalLengthSize, sps, pps, extension);
        }

        /// <summary>Serializes the record, with every reserved bit set to 1 as the spec requires.</summary>
        /// <exception cref="InvalidOperationException">A field doesn't fit its on-the-wire width.</exception>
        public byte[] ToArray()
        {
            if (NalLengthSize is not (1 or 2 or 4))
            {
                throw new InvalidOperationException($"{What}: NalLengthSize must be 1, 2 or 4, not {NalLengthSize}");
            }

            var writer = new ConfigurationRecordWriter(What);
            writer.WriteByte(1);
            writer.WriteByte(ProfileIndication);
            writer.WriteByte(ProfileCompatibility);
            writer.WriteByte(LevelIndication);
            writer.WriteByte(0xFC | (NalLengthSize - 1));
            writer.WriteNalUnits(SequenceParameterSets, countBits: 5, countPrefix: 0xE0, minimumCount: 1);
            writer.WriteNalUnits(PictureParameterSets, countBits: 8, countPrefix: 0, minimumCount: 0);

            if (HighProfileExtension is { } extension)
            {
                writer.WriteByte(0xFC | RequireRange(extension.ChromaFormat, 0, 3, nameof(extension.ChromaFormat)));
                writer.WriteByte(0xF8 | (RequireRange(extension.BitDepthLuma, 8, 15, nameof(extension.BitDepthLuma)) - 8));
                writer.WriteByte(0xF8 | (RequireRange(extension.BitDepthChroma, 8, 15, nameof(extension.BitDepthChroma)) - 8));
                writer.WriteNalUnits(extension.SequenceParameterSetExtensions, countBits: 8, countPrefix: 0, minimumCount: 0);
            }

            return writer.ToArray();
        }

        private static int RequireRange(int value, int min, int max, string name) =>
            value >= min && value <= max
                ? value
                : throw new InvalidOperationException($"{What}: {name} must be {min}-{max}, not {value}");
    }

    /// <summary>The high-profile fields at the end of an <see cref="AvcDecoderConfigurationRecord"/>.</summary>
    /// <param name="ChromaFormat">chroma_format (0-3, 1 = 4:2:0).</param>
    /// <param name="BitDepthLuma">Luma bit depth, 8-15.</param>
    /// <param name="BitDepthChroma">Chroma bit depth, 8-15.</param>
    /// <param name="SequenceParameterSetExtensions">SPS extension NAL units, usually none.</param>
    public sealed record AvcHighProfileExtension(
        int ChromaFormat,
        int BitDepthLuma,
        int BitDepthChroma,
        IReadOnlyList<ReadOnlyMemory<byte>> SequenceParameterSetExtensions);
}
