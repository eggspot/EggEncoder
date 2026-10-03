using System.Text;
using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class Crc32Test
    {
        [Fact]
        public void Compute_WellKnownTestVector_Should_Match()
        {
            // "123456789" -> 0xCBF43926 is the standard, widely cited test vector for this exact
            // CRC-32 variant (reflected poly 0xEDB88320, init/final-XOR 0xFFFFFFFF -- zlib/PNG/zip).
            var bytes = Encoding.ASCII.GetBytes("123456789");

            Crc32.Compute(bytes).Should().Be(0xCBF43926u);
        }

        [Fact]
        public void Compute_EmptyInput_Should_Be_Zero()
        {
            Crc32.Compute(ReadOnlySpan<byte>.Empty).Should().Be(0u);
        }

        [Fact]
        public void Compute_DifferentInputs_Should_Produce_DifferentResults()
        {
            var a = Crc32.Compute([1, 2, 3]);
            var b = Crc32.Compute([1, 2, 4]);

            a.Should().NotBe(b);
        }

        [Fact]
        public void Compute_SameInput_Should_Be_Deterministic()
        {
            var bytes = new byte[] { 10, 20, 30, 40, 50 };

            Crc32.Compute(bytes).Should().Be(Crc32.Compute(bytes));
        }
    }
}
