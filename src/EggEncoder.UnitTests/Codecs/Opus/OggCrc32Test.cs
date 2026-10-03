using EggEncoder.Codecs.Opus;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Opus
{
    public class OggCrc32Test
    {
        [Fact]
        public void Compute_RealFfmpegProducedOggPage_Should_Match_ActualChecksum()
        {
            // The first page (OpusHead) of a real ffmpeg -c:a libopus -produced .opus file, with
            // its own 4-byte CRC field (originally 0x4715a762, verified byte-for-byte against the
            // real file) zeroed out as the spec requires before computing. This is a genuinely
            // different algorithm from EggEncoder.Codecs.Tta.Crc32 (reflected/0xEDB88320) -- Ogg's
            // own variant is direct/non-reflected, polynomial 0x04C11DB7, init 0, no final XOR --
            // so this is the one real-world cross-check available for it, the same spirit as
            // FlacFfmpegCrossCheckTest, just at the byte level rather than a checked-in fixture
            // file (the full page is reproduced inline below instead).
            byte[] page =
            [
                0x4f, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xa4, 0xc6, 0x54, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // CRC field zeroed
                0x01, 0x13,
                0x4f, 0x70, 0x75, 0x73, 0x48, 0x65, 0x61, 0x64, 0x01, 0x01, 0x38, 0x01, 0x80, 0xbb,
                0x00, 0x00, 0x00, 0x00, 0x00
            ];

            OggCrc32.Compute(page).Should().Be(0x4715a762u);
        }

        [Fact]
        public void Compute_EmptyInput_Should_Return_Zero()
        {
            OggCrc32.Compute([]).Should().Be(0u);
        }

        [Fact]
        public void Compute_Should_Be_Deterministic()
        {
            byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

            OggCrc32.Compute(data).Should().Be(OggCrc32.Compute(data));
        }

        [Fact]
        public void Compute_DifferentInputs_Should_Produce_DifferentChecksums()
        {
            OggCrc32.Compute([1, 2, 3]).Should().NotBe(OggCrc32.Compute([1, 2, 4]));
        }
    }
}
