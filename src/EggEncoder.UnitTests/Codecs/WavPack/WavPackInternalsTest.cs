using EggEncoder.Codecs.WavPack;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    // Direct tests against this codec's internal building blocks, for malformed-input branches
    // that are impractical to trigger via a real (even corrupted) .wv fixture through the public
    // WavPackDecoder.Decode entry point -- see WavPackDecoderTest.cs for the fixture-based cases.
    public class WavPackInternalsTest
    {
        [Fact]
        public void EntropyDecoder_WithAnEscapedUnaryCodeTooLargeToRepresent_Should_Throw()
        {
            // A WavPackEntropyDecoder freshly constructed (median[0] == 0 for every channel, no
            // pending carry) immediately takes the zero-run-length escape path on its first call,
            // reading a capped 0-33 unary code followed, for a value of 2 or more, by an "extra
            // bits" extension. A bitstream of at least 33 consecutive one-bits drives that unary
            // read to its 33 cap, extending to 32 extra bits -- which this codebase's own 32-bit
            // int representation can't hold (and C#'s own `<<` operator would silently mask the
            // shift count to 0-31 rather than overflow), so this must throw rather than silently
            // produce a wrong value.
            var allOnes = Enumerable.Repeat((byte)0xFF, 16).ToArray();
            var reader = new WavPackBitReader(allOnes, 0, allOnes.Length);
            var entropy = new WavPackEntropyDecoder(1);

            var act = () => entropy.DecodeValue(reader, 0);

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("too large to represent");
        }

        [Fact]
        public void MetadataSubBlock_ReadAll_WithTruncatedSubBlockHeader_Should_Throw()
        {
            // A single byte declaring a "large" (3-byte size field) sub-block id, with nothing
            // after it -- not even the size field itself, let alone any data.
            byte[] data = [0x80];

            var act = () => WavPackMetadataSubBlock.ReadAll(data, 0, data.Length).ToList();

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("id/size header");
        }

        [Fact]
        public void MetadataSubBlock_ReadAll_WithSizeExtendingPastTheMetadataRegion_Should_Throw()
        {
            // A small (1-byte size field) sub-block declaring 10 words (20 bytes) of data, but the
            // metadata region this is read from only extends 4 bytes past the header -- far short
            // of what's declared.
            byte[] data = [0x02, 10, 0, 0, 0, 0];

            var act = () => WavPackMetadataSubBlock.ReadAll(data, 0, data.Length).ToList();

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("extends past the block's own metadata region");
        }

        [Fact]
        public void BlockDecoder_WithDecorrWeightsBeforeDecorrTerms_Should_Throw()
        {
            // WP_ID_DECORR_WEIGHTS (id 0x03) appearing with no preceding WP_ID_DECORR_TERMS (id
            // 0x02) at all -- a real encoder always writes terms first (weights/samples describe
            // those same terms), so this only happens for a malformed/reordered file.
            byte[] data = [0x03, 1, 0, 0]; // id=WP_ID_DECORR_WEIGHTS, size=1 word, 2 bytes of data
            var header = MakeMonoHeader();

            var act = () => WavPackBlockDecoder.Decode(data, 0, data.Length, header, out _, out _);

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("WP_ID_DECORR_WEIGHTS metadata appears before its WP_ID_DECORR_TERMS");
        }

        [Fact]
        public void BlockDecoder_WithDecorrSamplesBeforeDecorrTerms_Should_Throw()
        {
            byte[] data = [0x04, 1, 0, 0]; // id=WP_ID_DECORR_SAMPLES, size=1 word, 2 bytes of data
            var header = MakeMonoHeader();

            var act = () => WavPackBlockDecoder.Decode(data, 0, data.Length, header, out _, out _);

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("WP_ID_DECORR_SAMPLES metadata appears before its WP_ID_DECORR_TERMS");
        }

        [Fact]
        public void BlockDecoder_WithTruncatedEntropyVars_Should_Throw()
        {
            // A mono block needs 3 median values (6 bytes) in its WP_ID_ENTROPY_VARS sub-block;
            // this one declares only 1 word (2 bytes).
            byte[] data = [0x05, 1, 0, 0]; // id=WP_ID_ENTROPY_VARS, size=1 word, 2 bytes of data
            var header = MakeMonoHeader();

            var act = () => WavPackBlockDecoder.Decode(data, 0, data.Length, header, out _, out _);

            act.Should().ThrowExactly<InvalidDataException>()
                .Which.Message.Should().Contain("WP_ID_ENTROPY_VARS metadata is");
        }

        // bit 2 (0x4) = mono; bits 11/12 (0x800/0x1000) = initial+final block of sequence; bits
        // 0-1 = 01 (2 bytes/sample, 16-bit) -- a minimal, otherwise-unremarkable mono block header.
        private static WavPackBlockHeader MakeMonoHeader() => WavPackBlockHeader.Parse(
            [
                (byte)'w', (byte)'v', (byte)'p', (byte)'k', // ckID
                0, 0, 0, 0, // ckSize (unused by these tests)
                0, 0, // version
                0, 0, // block_index_u8, total_samples_u8
                1, 0, 0, 0, // total_samples (low 32 bits)
                0, 0, 0, 0, // block_index (low 32 bits)
                4, 0, 0, 0, // block_samples
                0x05, 0x18, 0x00, 0x00, // flags: mono | initial | final | 16-bit
                0, 0, 0, 0, // crc
            ],
            0);
    }
}
