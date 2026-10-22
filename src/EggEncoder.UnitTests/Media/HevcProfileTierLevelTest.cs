using EggEncoder.Media;
using FluentAssertions;

namespace EggEncoder.UnitTests.Media
{
    public class HevcProfileTierLevelTest
    {
        [Fact]
        public void ToCodecsString_SpecExample_Should_Match_Iso14496_15_AnnexE()
        {
            // ISO/IEC 14496-15 Annex E's own example: profile space 1, profile 4, compatibility
            // flags reversed to 0x41, High tier level 120, constraint bytes B0 23.
            var ptl = new HevcProfileTierLevel(1, true, 4, 0x82000000, 0xB02300000000, 120);

            ptl.ToCodecsString("hev1").Should().Be("hev1.A4.41.H120.B0.23");
        }

        [Fact]
        public void ToCodecsString_MainProfile_Should_Match_Common_Hls_Form()
        {
            new HevcProfileTierLevel(0, false, 1, 0x60000000, 0xB00000000000, 93).ToCodecsString()
                .Should().Be("hvc1.1.6.L93.B0");
        }

        [Fact]
        public void ToCodecsString_NoConstraintFlags_Should_Omit_Every_Constraint_Byte()
        {
            new HevcProfileTierLevel(3, false, 2, 0, 0, 30).ToCodecsString().Should().Be("hvc1.C2.0.L30");
        }

        [Fact]
        public void ToCodecsString_InnerZeroConstraintByte_Should_Be_Kept()
        {
            new HevcProfileTierLevel(2, false, 1, 0x40000000, 0x900001000000, 60).ToCodecsString()
                .Should().Be("hvc1.B1.2.L60.90.0.1");
        }
    }
}
