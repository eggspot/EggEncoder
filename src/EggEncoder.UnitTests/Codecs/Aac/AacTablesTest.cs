using EggEncoder.Codecs.Aac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Aac
{
    public class AacTablesTest
    {
        [Fact]
        public void ScalefactorHuffman_Should_ConstructWithoutThrowing()
        {
            AacTables.ScalefactorHuffman.Should().NotBeNull();
        }

        [Fact]
        public void SpectralCodebooks_Should_HaveElevenEntries_AllConstructedWithoutThrowing()
        {
            AacTables.SpectralCodebooks.Should().HaveCount(11);

            foreach (var codebook in AacTables.SpectralCodebooks)
            {
                codebook.Huffman.Should().NotBeNull();
            }
        }

        [Fact]
        public void SpectralCodebooks_GroupSizeAndLav_Should_MatchExpectedAacStructure()
        {
            (int GroupSize, int Lav, bool SignedInTable, bool HasEscape)[] expected =
            [
                (4, 1, true, false),
                (4, 1, true, false),
                (4, 2, false, false),
                (4, 2, false, false),
                (2, 4, true, false),
                (2, 4, true, false),
                (2, 7, false, false),
                (2, 7, false, false),
                (2, 12, false, false),
                (2, 12, false, false),
                (2, 16, false, true)
            ];

            for (var i = 0; i < expected.Length; i++)
            {
                var codebook = AacTables.SpectralCodebooks[i];
                codebook.GroupSize.Should().Be(expected[i].GroupSize, $"codebook {i + 1} group size");
                codebook.Lav.Should().Be(expected[i].Lav, $"codebook {i + 1} lav");
                codebook.SignedInTable.Should().Be(expected[i].SignedInTable, $"codebook {i + 1} signed-in-table");
                codebook.HasEscape.Should().Be(expected[i].HasEscape, $"codebook {i + 1} has-escape");
            }
        }

        [Fact]
        public void SampleRates_Should_MatchKnownMpeg4AudioTable()
        {
            int[] expected = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

            AacTables.SampleRates.Should().Equal(expected);
        }

        [Fact]
        public void ScalefactorBandOffsets_Should_StartAtZero_And_EndAt1024()
        {
            var offsets = AacTables.ScalefactorBandOffsets1024_44100Or48000;

            offsets[0].Should().Be(0);
            offsets[^1].Should().Be(1024);
            offsets.Should().HaveCount(50);
        }
    }
}
