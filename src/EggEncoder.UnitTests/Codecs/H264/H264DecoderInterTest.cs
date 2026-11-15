using FluentAssertions;
using static EggEncoder.UnitTests.Codecs.H264.H264TestStreams;

namespace EggEncoder.UnitTests.Codecs.H264
{
    public class H264DecoderInterTest
    {
        public static TheoryData<string, string[]> FfmpegFrames => new()
        {
            { "p_basic.h264", ["87e7ad902c86207e7f869a0368d65408", "d2a07d6f9843afa8ecf7adab8308bcfc", "08c226f4d207c2e0bb6e15c709ef6a8c", "cc665923feaf0deb2140104de64a4854", "9aa569ff83042c5a50285c69571ccf46", "c6af5ef21973d3fda7058e61d5d27cd6", "c00ae1ceff937eae732bd2e4b453556a", "9ef5bf12d38548d20a1923dfc82427a0"] },
            { "p_multiref.h264", ["de5f75d91b9861bed1a60dac41e63ca2", "2cf091d81a870b80435034a5f7a65a9c", "9eeb55ea2e58beda5a04f1f63d6a46bd", "b305407385152b3131e45798cc9bdcde", "e486a99e631b0c126790318104f89bb8", "2508eb1cba3b53dddc4a82236ea87c6e", "a574e588e8794883a9b8766c7c05639d", "2801e7033b45a42bd8267088f99c25ab"] },
            { "p_slices.h264", ["54af7c20865242b61ff581df31e62845", "be81c41e8115a01aa7280f6749199674", "7fc12d6993e158422a9610d4b2c53276", "a6fbb7b9dcde8a83bdc8f7e4b9a67fa3", "5e00debaa39f91e3b450d79f1b6b1fe0", "fcda4c74c0c3a6e01ff1d1a64f40f694", "09088d27385f7068808026ed0fc06763", "966e22248efe5294d69dfcb755d7c603"] },
            { "p_static.h264", Enumerable.Repeat("dc544ed9f0a8c14bdad469243f0f8f27", 8).ToArray() },
            { "p_weighted.h264", ["910380cd4c3a8eee2559b7cb16ccd3b2", "9550b5e42653bbaa4c4acfd1627c0bd6", "1f4d314b73af995c02a250e31139527f", "7ae1fa95abb7a61a812cb372eb25820d", "0084550a3b91e43cd6dbbbb69a3f837e", "bf5f388250f327fcacecf9f51b2117d3", "69efdcac1bfa9c0a946dfb7149dc56af", "6440704ae53bf8b618d8792de7c0bf05"] },
            { "p_reorder.h264", ["de5f75d91b9861bed1a60dac41e63ca2", "2cf091d81a870b80435034a5f7a65a9c", "e1da95ca0d23da82b70a7ebe73b3bfff", "3494b5d646a5099d449517e85c96e912", "629fc980800e98da7b383a7fa20ba7b4", "2dab775e31d975a83422a38c339110ae", "575acefada4b8125382c2e6740a348e9", "48af0d49cba7871f7562e34ed07ec672"] },
            { "p_longterm.h264", ["de5f75d91b9861bed1a60dac41e63ca2", "2cf091d81a870b80435034a5f7a65a9c", "9eeb55ea2e58beda5a04f1f63d6a46bd", "b305407385152b3131e45798cc9bdcde", "c50bf26ebcb7152884b8cc7e7de21c64", "1477a7c4b78a597cb955cf18551943f7", "7c8be2a8c47fbc65021c433fdde9d799", "0a08efbbd566b6f36ae1dd171984dedf"] },
            { "p_cropped.h264", ["d47d2395e4a01a5d1dbbcfe90a0ad5b7", "feb9f2d96ebe037b0f7a188da03d7877", "2985d04fd605ea78c95b9b7c7f82a3c9", "e8adfadc93d6e67e895dbae14785dcb3", "d1fdc4a42b1b4ce38d01bef7e45d065f", "4285f64dab082302592babaa70a20d3e", "358f5eaa1c040fec93832f034e2af0ba", "7c9138d752d06d9a2ecfc7ca14062fc8"] }
        };

        [Theory]
        [MemberData(nameof(FfmpegFrames))]
        public void Decode_P_Streams_Should_Match_Ffmpeg_Bit_For_Bit(string fixture, string[] expected)
        {
            DecodeToMd5(Fixture(fixture)).Should().Equal(expected);
        }
    }
}
