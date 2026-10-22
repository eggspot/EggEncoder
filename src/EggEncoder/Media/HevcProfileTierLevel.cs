using System.Text;

namespace EggEncoder.Media
{
    /// <summary>
    /// The general profile/tier/level fields of an H.265 stream (ITU-T H.265 clause 7.3.3), which
    /// both an SPS and an <see cref="HevcDecoderConfigurationRecord"/> carry, and which together
    /// make up the stream's RFC 6381 codec string.
    /// </summary>
    /// <param name="ProfileSpace">general_profile_space, 0-3.</param>
    /// <param name="TierFlag">general_tier_flag: false for Main tier, true for High tier.</param>
    /// <param name="ProfileIdc">general_profile_idc (1 = Main, 2 = Main 10, ...).</param>
    /// <param name="ProfileCompatibilityFlags">general_profile_compatibility_flag[0..31], flag 0 in the most significant bit.</param>
    /// <param name="ConstraintIndicatorFlags">The 48 bits from general_progressive_source_flag onward, first bit most significant.</param>
    /// <param name="LevelIdc">general_level_idc: 30 times the level number (120 = level 4).</param>
    public readonly record struct HevcProfileTierLevel(
        int ProfileSpace,
        bool TierFlag,
        int ProfileIdc,
        uint ProfileCompatibilityFlags,
        ulong ConstraintIndicatorFlags,
        int LevelIdc)
    {
        /// <summary>
        /// The codec string per ISO/IEC 14496-15 Annex E (which RFC 6381 defers to), for an HLS
        /// <c>CODECS</c> attribute or an MP4 sample entry, e.g. <c>hvc1.1.6.L93.B0</c>: profile space
        /// letter + profile, the compatibility flags bit-reversed in hex, tier letter + level, then
        /// each constraint byte in hex with trailing zero bytes omitted.
        /// </summary>
        public string ToCodecsString(string sampleEntryFourCc = "hvc1")
        {
            var builder = new StringBuilder(sampleEntryFourCc).Append('.');
            if (ProfileSpace > 0)
            {
                builder.Append((char)('A' + ProfileSpace - 1));
            }

            builder.Append(ProfileIdc)
                .Append('.').Append(ReverseBits(ProfileCompatibilityFlags).ToString("X"))
                .Append('.').Append(TierFlag ? 'H' : 'L').Append(LevelIdc);

            var lastNonZeroByte = -1;
            for (var i = 0; i < 6; i++)
            {
                if (ConstraintByte(i) != 0)
                {
                    lastNonZeroByte = i;
                }
            }

            for (var i = 0; i <= lastNonZeroByte; i++)
            {
                builder.Append('.').Append(ConstraintByte(i).ToString("X"));
            }

            return builder.ToString();
        }

        private byte ConstraintByte(int index) => (byte)(ConstraintIndicatorFlags >> (40 - (8 * index)));

        private static uint ReverseBits(uint value)
        {
            var reversed = 0u;
            for (var i = 0; i < 32; i++)
            {
                reversed = (reversed << 1) | ((value >> i) & 1);
            }

            return reversed;
        }
    }
}
