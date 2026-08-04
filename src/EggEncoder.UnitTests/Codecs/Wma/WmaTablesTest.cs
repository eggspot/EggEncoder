using EggEncoder.Codecs.Wma;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wma
{
    public class WmaTablesTest
    {
        [Fact]
        public void Coef4Huffman_Should_Construct_Without_Throwing()
        {
            WmaTables.Coef4Huffman.Should().NotBeNull();
        }

        [Fact]
        public void CriticalFrequencies_Should_Have25Entries()
        {
            WmaTables.CriticalFrequencies.Should().HaveCount(25);
        }

        [Fact]
        public void CriticalFrequencies_Should_MatchKnownWmaBarkScaleTable()
        {
            ushort[] expected =
            [
                100, 200, 300, 400, 510, 630, 770, 920,
                1080, 1270, 1480, 1720, 2000, 2320, 2700, 3150,
                3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500,
                24500
            ];

            WmaTables.CriticalFrequencies.Should().Equal(expected);
        }

        [Fact]
        public void CriticalFrequencies_Should_BeStrictlyIncreasing()
        {
            for (var i = 1; i < WmaTables.CriticalFrequencies.Length; i++)
            {
                WmaTables.CriticalFrequencies[i].Should().BeGreaterThan(WmaTables.CriticalFrequencies[i - 1], $"frequency at index {i} is not strictly greater than the previous one");
            }
        }

        [Fact]
        public void Coef4Levels_Should_SumToRunLevelTableSizeMinusReservedEscapeAndEobCodes()
        {
            var sum = 0;
            foreach (var runLength in WmaTables.Coef4Levels)
            {
                sum += runLength;
            }

            sum.Should().Be(WmaTables.Coef4Bits.Length - 2);
        }

        [Fact]
        public void Coef4Levels_Should_BeMonotonicallyNonIncreasing()
        {
            for (var i = 1; i < WmaTables.Coef4Levels.Length; i++)
            {
                WmaTables.Coef4Levels[i].Should().BeLessThanOrEqualTo(WmaTables.Coef4Levels[i - 1], $"run length at index {i} increased, expected a non-increasing sequence (Huffman codes are ordered from most to least frequent)");
            }
        }
    }
}
