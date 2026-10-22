namespace EggEncoder.Media
{
    /// <summary>
    /// An H.265 <c>HEVCDecoderConfigurationRecord</c> (ISO/IEC 14496-15 clause 8.3.3.1): the codec
    /// configuration Enhanced-FLV/RTMP sequence headers, MP4 <c>hvcC</c> boxes and
    /// <see cref="MediaTrackInfo.CodecConfiguration"/> all carry for H.265. <see cref="Parse"/> and
    /// <see cref="ToArray"/> are exact inverses for any well-formed record.
    /// </summary>
    /// <param name="ProfileTierLevel">The general profile, tier and level.</param>
    /// <param name="MinSpatialSegmentationIdc">min_spatial_segmentation_idc, 0-4095.</param>
    /// <param name="ParallelismType">parallelismType, 0-3.</param>
    /// <param name="ChromaFormat">chroma_format_idc, 0-3.</param>
    /// <param name="BitDepthLuma">Luma bit depth, 8-15.</param>
    /// <param name="BitDepthChroma">Chroma bit depth, 8-15.</param>
    /// <param name="AverageFrameRate">avgFrameRate in frames per 256 seconds; 0 when unspecified.</param>
    /// <param name="ConstantFrameRate">constantFrameRate, 0-3.</param>
    /// <param name="NumTemporalLayers">numTemporalLayers, 0-7.</param>
    /// <param name="TemporalIdNested">temporalIdNested.</param>
    /// <param name="NalLengthSize">Bytes per NAL length prefix in this track's packets: 1, 2 or 4.</param>
    /// <param name="Arrays">The parameter-set arrays (VPS, SPS, PPS, SEI), in record order.</param>
    public sealed record HevcDecoderConfigurationRecord(
        HevcProfileTierLevel ProfileTierLevel,
        int MinSpatialSegmentationIdc,
        int ParallelismType,
        int ChromaFormat,
        int BitDepthLuma,
        int BitDepthChroma,
        int AverageFrameRate,
        int ConstantFrameRate,
        int NumTemporalLayers,
        bool TemporalIdNested,
        int NalLengthSize,
        IReadOnlyList<HevcNalUnitArray> Arrays)
    {
        /// <summary>NAL unit type of a video parameter set.</summary>
        public const int VpsNalUnitType = 32;

        /// <summary>NAL unit type of a sequence parameter set.</summary>
        public const int SpsNalUnitType = 33;

        /// <summary>NAL unit type of a picture parameter set.</summary>
        public const int PpsNalUnitType = 34;

        private const string What = "HEVCDecoderConfigurationRecord";

        /// <summary>Every NAL unit of the given type across all arrays, in record order.</summary>
        public IReadOnlyList<ReadOnlyMemory<byte>> GetNalUnits(int nalUnitType) =>
            Arrays.Where(array => array.NalUnitType == nalUnitType).SelectMany(array => array.NalUnits).ToArray();

        /// <summary>Parses a record. Every NAL unit is copied, so the result doesn't alias <paramref name="data"/>.</summary>
        /// <exception cref="InvalidDataException">Truncated, an unknown version, an invalid length size, no SPS, or a NAL unit length past the end.</exception>
        public static HevcDecoderConfigurationRecord Parse(ReadOnlySpan<byte> data)
        {
            var reader = new ConfigurationRecordReader(data, What);
            var version = reader.ReadByte();
            if (version != 1)
            {
                throw new InvalidDataException($"{What}: unsupported configurationVersion {version}");
            }

            var profileByte = reader.ReadByte();
            var compatibilityFlags = (uint)reader.ReadBigEndian(4);
            var constraintFlags = reader.ReadBigEndian(6);
            var levelIdc = reader.ReadByte();
            var profileTierLevel = new HevcProfileTierLevel(
                profileByte >> 6, (profileByte & 0x20) != 0, profileByte & 0x1F, compatibilityFlags, constraintFlags, levelIdc);

            var minSpatialSegmentation = reader.ReadUInt16() & 0x0FFF;
            var parallelismType = reader.ReadByte() & 0x03;
            var chromaFormat = reader.ReadByte() & 0x03;
            var bitDepthLuma = (reader.ReadByte() & 0x07) + 8;
            var bitDepthChroma = (reader.ReadByte() & 0x07) + 8;
            var averageFrameRate = reader.ReadUInt16();
            var packed = reader.ReadByte();
            var nalLengthSize = (packed & 0x03) + 1;
            if (nalLengthSize == 3)
            {
                throw new InvalidDataException($"{What}: lengthSizeMinusOne of 2 is not allowed");
            }

            var arrayCount = reader.ReadByte();
            var arrays = new HevcNalUnitArray[arrayCount];
            for (var i = 0; i < arrayCount; i++)
            {
                var header = reader.ReadByte();
                var nalUnitCount = reader.ReadUInt16();
                arrays[i] = new HevcNalUnitArray((header & 0x80) != 0, header & 0x3F, reader.ReadNalUnits(nalUnitCount));
            }

            var record = new HevcDecoderConfigurationRecord(
                profileTierLevel,
                minSpatialSegmentation,
                parallelismType,
                chromaFormat,
                bitDepthLuma,
                bitDepthChroma,
                averageFrameRate,
                packed >> 6,
                (packed >> 3) & 0x07,
                (packed & 0x04) != 0,
                nalLengthSize,
                arrays);

            if (record.GetNalUnits(SpsNalUnitType).Count == 0)
            {
                throw new InvalidDataException($"{What}: no sequence parameter set");
            }

            return record;
        }

        /// <summary>Serializes the record, with every reserved bit set to 1 as the spec requires.</summary>
        /// <exception cref="InvalidOperationException">A field doesn't fit its on-the-wire width.</exception>
        public byte[] ToArray()
        {
            var ptl = ProfileTierLevel;
            if (ptl.ProfileSpace is < 0 or > 3 || ptl.ProfileIdc is < 0 or > 31 || ptl.ConstraintIndicatorFlags > 0xFFFF_FFFF_FFFFUL)
            {
                throw new InvalidOperationException($"{What}: ProfileTierLevel fields don't fit their on-the-wire widths");
            }

            if (NalLengthSize is not (1 or 2 or 4))
            {
                throw new InvalidOperationException($"{What}: NalLengthSize must be 1, 2 or 4, not {NalLengthSize}");
            }

            ArgumentNullException.ThrowIfNull(Arrays);
            var writer = new ConfigurationRecordWriter(What);
            writer.WriteByte(1);
            writer.WriteByte((ptl.ProfileSpace << 6) | (ptl.TierFlag ? 0x20 : 0) | ptl.ProfileIdc);
            writer.WriteBigEndian(ptl.ProfileCompatibilityFlags, 4);
            writer.WriteBigEndian(ptl.ConstraintIndicatorFlags, 6);
            writer.WriteByte(ptl.LevelIdc);
            writer.WriteUInt16(0xF000 | RequireRange(MinSpatialSegmentationIdc, 0, 0x0FFF, nameof(MinSpatialSegmentationIdc)));
            writer.WriteByte(0xFC | RequireRange(ParallelismType, 0, 3, nameof(ParallelismType)));
            writer.WriteByte(0xFC | RequireRange(ChromaFormat, 0, 3, nameof(ChromaFormat)));
            writer.WriteByte(0xF8 | (RequireRange(BitDepthLuma, 8, 15, nameof(BitDepthLuma)) - 8));
            writer.WriteByte(0xF8 | (RequireRange(BitDepthChroma, 8, 15, nameof(BitDepthChroma)) - 8));
            writer.WriteUInt16(AverageFrameRate);
            writer.WriteByte(
                (RequireRange(ConstantFrameRate, 0, 3, nameof(ConstantFrameRate)) << 6)
                | (RequireRange(NumTemporalLayers, 0, 7, nameof(NumTemporalLayers)) << 3)
                | (TemporalIdNested ? 0x04 : 0)
                | (NalLengthSize - 1));
            writer.WriteByte(Arrays.Count);
            foreach (var array in Arrays)
            {
                writer.WriteByte((array.ArrayCompleteness ? 0x80 : 0) | RequireRange(array.NalUnitType, 0, 63, nameof(array.NalUnitType)));
                writer.WriteUInt16(array.NalUnits.Count);
                writer.WriteNalUnitList(array.NalUnits);
            }

            return writer.ToArray();
        }

        private static int RequireRange(int value, int min, int max, string name) =>
            value >= min && value <= max
                ? value
                : throw new InvalidOperationException($"{What}: {name} must be {min}-{max}, not {value}");
    }

    /// <summary>One parameter-set array of an <see cref="HevcDecoderConfigurationRecord"/>.</summary>
    /// <param name="ArrayCompleteness">True when every NAL unit of this type is in the array, none in band.</param>
    /// <param name="NalUnitType">The NAL unit type of every unit in the array (32 VPS, 33 SPS, 34 PPS, 39/40 SEI).</param>
    /// <param name="NalUnits">The NAL units, each with its two-byte NAL header.</param>
    public sealed record HevcNalUnitArray(bool ArrayCompleteness, int NalUnitType, IReadOnlyList<ReadOnlyMemory<byte>> NalUnits);
}
