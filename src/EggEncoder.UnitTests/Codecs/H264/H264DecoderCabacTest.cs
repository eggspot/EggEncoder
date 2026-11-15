using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264DecoderCabacTest
    {
        public static TheoryData<string, string[]> FfmpegFrames => new()
        {
            { "cabac_intra.h264", ["fffd66ba2cefe98f2761a6dfb385014a", "58121dfb3484f3bf89f52cc3f5efbf40", "b450e4b6faa22b892d7a2e53af118802", "2de87ff601b4ee727b81a6c12bae81e0", "5d3e0fdac0b5e280e5479ccc5b9d77f6", "b888e19f731f7c60eaa186e4a0696c5e", "000e2f84d51991a505b61969d2d7c1b1", "f4ba391aa378a38b9b0dfdf3b97ac162"] },
            { "cabac_intra_lowqp.h264", ["257bb4826f2e8a004b9831a57378318d", "1af285fbebd3d87002d2ecfbf7e067e2"] },
            { "cabac_p.h264", ["23dc150b72cd5684ab4ede7770b2df18", "23dc150b72cd5684ab4ede7770b2df18", "23dc150b72cd5684ab4ede7770b2df18", "23dc150b72cd5684ab4ede7770b2df18", "4a72602fab6160e6159f0b7749ca3bc8", "d7fa5021e6238bcd39bed455674221c7", "7cd6f8741c7bc0218e0e3f43e1c8a2f6", "421aa55855bec05e3c46fb806e6de50e"] },
            { "cabac_p_multiref.h264", ["c657939d0fdbe567a652f8fac1b552cb", "492ac2cd527db4db27a5257d933a61f2", "6ed27fb495047ff8c316bccbc99cea32", "9310c454ad6348485047f0e9b8a9cdb5", "37477db8df21f999421b7e9c9c914bd1", "5c16ba0fecee025676de01826ac6cb77", "cb33f1a0960628e089f6574bde6c496d", "6a06e8d3d0a0f2a9bbc58b45a2b5fd4c"] },
            { "cabac_p_idc2.h264", ["910380cd4c3a8eee2559b7cb16ccd3b2", "31ca8b0b9078a082e769b04bb7368420", "3fc801a48af7c2b3d4f137fcdb94a33e", "24ff41a7aa94598ea9b3601b9bd54d0d", "d2d59e4de0d795ecf7b8b5eb103e3e9f", "7928b8f8547c9fa1266f8c2570d2f9fd", "a4999a14f97c94944e66ab5787b4f5ea", "81575c1f98bed6db6e5951ce3cbd77b6"] },
            { "cabac_p_highqp.h264", ["4828f69c19e732de7d1014bcf1342024", "4828f69c19e732de7d1014bcf1342024", "4828f69c19e732de7d1014bcf1342024", "fdc8ae1c70252b939b3b0c07789cb8be", "fdc8ae1c70252b939b3b0c07789cb8be", "fdc8ae1c70252b939b3b0c07789cb8be", "fdc8ae1c70252b939b3b0c07789cb8be", "fdc8ae1c70252b939b3b0c07789cb8be"] },
            { "cabac_p_static.h264", Enumerable.Repeat("f3188abb862ca3ccf200a473584c46dc", 8).ToArray() },
            { "cabac_cropped.h264", ["3910ccb7fb60b0a9a723cf1e11af5f5d", "a9c131b3103e30c93a3e6e634d63792a", "af6999de3347ade35257ce7321b60350", "ffd8dc37c50dc66f4de08761c69cde4d", "6e6ec170fac6c306cd68ee66ceece35e", "a11a671608fdf485d0f77158981ad7f3", "8230fe077315e547edd22b493545ea29", "c6ccd2705388488b5d402fea45e6f64c"] }
        };

        [Theory]
        [MemberData(nameof(FfmpegFrames))]
        public void Decode_Cabac_Streams_Should_Match_Ffmpeg_Bit_For_Bit(string fixture, string[] expected)
        {
            DecodeToMd5(Fixture(fixture)).Should().Equal(expected);
        }
    }
}
