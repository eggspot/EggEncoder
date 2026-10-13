using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackMetadataSubBlockTest
    {
        [Fact]
        public void ReadAll_WithOddFlagAndZeroWordCount_Should_ThrowInvalidDataException_NotArgumentOutOfRangeException()
        {
            // id byte: 0x40 (isOdd) | 0x02 (IdDecorrTerms, arbitrary), not large (no 0x80) -- a
            // small (non-large) sub-block header is id(1) + size-in-words(1). wordCount = 0 with
            // isOdd set means "shave one padding byte off a word-based size that's already zero",
            // which is meaningless -- before this fix, byteCount computed as -1 and reached
            // `new ArraySegment<byte>(data, dataStart, -1)`, throwing the BCL's own
            // ArgumentOutOfRangeException rather than this codebase's usual InvalidDataException.
            byte[] data = [0x42, 0x00];

            var act = () => WavPackMetadataSubBlock.ReadAll(data, 0, data.Length).ToList();

            act.Should().ThrowExactly<InvalidDataException>().WithMessage("*zero word count*");
        }

        [Fact]
        public void ReadAll_WithOddFlagAndNonZeroWordCount_Should_ReturnOneLessByte()
        {
            // The legitimate case this flag exists for: wordCount=1 (2 bytes) with isOdd set means
            // the real data is only 1 byte, not 2 -- confirming the fix's `byteCount < 0` guard
            // doesn't reject this valid combination.
            byte[] data = [0x42, 0x01, 0xAB, 0x00];

            var subBlocks = WavPackMetadataSubBlock.ReadAll(data, 0, data.Length).ToList();

            subBlocks.Should().HaveCount(1);
            subBlocks[0].Data.Should().Equal(0xAB);
        }
    }
}
