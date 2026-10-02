using EggEncoder.Codecs.Alac;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Alac
{
    public class CafWriterTest
    {
        private static readonly AlacSpecificConfig Config = new()
        {
            FrameLength = 4096,
            BitDepth = 16,
            Pb = 40,
            Mb = 10,
            Kb = 14,
            NumChannels = 1,
            MaxRun = 255,
            SampleRate = 44100
        };

        [Fact]
        public void Write_Then_CafReader_Open_Should_RoundTrip_Config_And_SmallPackets()
        {
            // Packet sizes deliberately span the single-byte/multi-byte VLQ boundary (127 vs 128).
            var packets = new[] { new byte[1], new byte[127], new byte[128], new byte[300] };

            var filePath = Path.Combine(Path.GetTempPath(), $"caf_writer_{Guid.NewGuid():N}.caf");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    CafWriter.Write(stream, Config, packets, totalValidFrames: 12345);
                }

                using var reader = CafReader.Open(filePath);
                reader.Config.FrameLength.Should().Be(Config.FrameLength);
                reader.Config.BitDepth.Should().Be(Config.BitDepth);
                reader.Config.SampleRate.Should().Be(Config.SampleRate);
                reader.TotalValidFrames.Should().Be(12345);

                foreach (var expectedPacket in packets)
                {
                    reader.HasMorePackets.Should().BeTrue();
                    reader.ReadNextPacket().Should().Equal(expectedPacket);
                }

                reader.HasMorePackets.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Write_With_NoPackets_Should_Produce_A_ReadableEmptyFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"caf_writer_empty_{Guid.NewGuid():N}.caf");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    CafWriter.Write(stream, Config, [], totalValidFrames: 0);
                }

                using var reader = CafReader.Open(filePath);
                reader.TotalValidFrames.Should().Be(0);
                reader.HasMorePackets.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
