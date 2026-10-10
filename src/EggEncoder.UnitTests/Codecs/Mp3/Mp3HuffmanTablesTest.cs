using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Mp3HuffmanTablesTest
    {
        // Transform.HuffmanTable's own constructor throws if a table's code lengths don't sum to
        // exactly 1.0 under Kraft's inequality, or (indirectly, via GetCode never finding a usable
        // entry) if any entry is missing -- so simply accessing every one of this project's own
        // transcribed tables without an exception is a genuine, cheap regression check that the
        // ISO/IEC 11172-3 Annex I data in Mp3HuffmanTables.cs was transcribed correctly.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(15)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(18)]
        [InlineData(19)]
        [InlineData(20)]
        [InlineData(21)]
        [InlineData(22)]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(27)]
        [InlineData(28)]
        [InlineData(29)]
        [InlineData(30)]
        [InlineData(31)]
        public void GetBigValueTable_EveryRealTableSelectIndex_Should_NotThrow(int tableSelect)
        {
            var act = () => Mp3HuffmanTables.GetBigValueTable(tableSelect);

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(14)]
        [InlineData(32)]
        public void GetBigValueTable_ReservedOrOutOfRangeIndex_Should_Throw(int tableSelect)
        {
            var act = () => Mp3HuffmanTables.GetBigValueTable(tableSelect);

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void TableA_And_TableB_Should_BeAccessible()
        {
            Mp3HuffmanTables.TableA.Should().NotBeNull();
            Mp3HuffmanTables.TableB.Should().NotBeNull();
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(3, 3)]
        [InlineData(5, 4)]
        [InlineData(6, 4)]
        [InlineData(7, 6)]
        [InlineData(8, 6)]
        [InlineData(9, 6)]
        [InlineData(10, 8)]
        [InlineData(11, 8)]
        [InlineData(12, 8)]
        [InlineData(13, 16)]
        [InlineData(15, 16)]
        [InlineData(16, 16)]
        [InlineData(24, 16)]
        public void GetBigValueTable_Should_ReportTheSpecsOwnPairWidth(int tableSelect, int expectedWidth)
        {
            var (_, width, _) = Mp3HuffmanTables.GetBigValueTable(tableSelect);

            width.Should().Be(expectedWidth);
        }

        [Theory]
        [InlineData(16, 1)]
        [InlineData(17, 2)]
        [InlineData(18, 3)]
        [InlineData(19, 4)]
        [InlineData(20, 6)]
        [InlineData(21, 8)]
        [InlineData(22, 10)]
        [InlineData(23, 13)]
        [InlineData(24, 4)]
        [InlineData(25, 5)]
        [InlineData(26, 6)]
        [InlineData(27, 7)]
        [InlineData(28, 8)]
        [InlineData(29, 9)]
        [InlineData(30, 11)]
        [InlineData(31, 13)]
        public void GetBigValueTable_Should_ReportTheSpecsOwnLinbits(int tableSelect, int expectedLinbits)
        {
            var (_, _, linbits) = Mp3HuffmanTables.GetBigValueTable(tableSelect);

            linbits.Should().Be(expectedLinbits);
        }
    }
}
