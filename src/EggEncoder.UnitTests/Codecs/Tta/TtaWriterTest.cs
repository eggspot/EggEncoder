using EggEncoder.Codecs.Tta;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Tta
{
    public class TtaWriterTest
    {
        [Fact]
        public void Write_Then_TtaReader_Open_Should_RoundTrip_Config_And_Frames()
        {
            // TTA's header has no explicit frame-count field -- TtaReader derives how many seek-table
            // entries to expect from totalSamples/frameLength, so totalSamples and the frame count
            // here must agree for this to be a valid file: sampleRate=245 gives frameLength=256
            // exactly (245*256/245), and totalSamples=700 needs ceil(700/256)=3 frames, matching the
            // 3 frames below.
            var frames = new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6, 7, 8 }, new byte[1] };

            var filePath = Path.Combine(Path.GetTempPath(), $"tta_writer_{Guid.NewGuid():N}.tta");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    TtaWriter.Write(stream, channels: 1, bitsPerSample: 16, sampleRate: 245, totalSamples: 700, frames);
                }

                using var reader = TtaReader.Open(filePath);
                reader.Channels.Should().Be(1);
                reader.BitsPerSample.Should().Be(16);
                reader.SampleRate.Should().Be(245);
                reader.TotalSamples.Should().Be(700);

                foreach (var expectedFrame in frames)
                {
                    reader.HasMoreFrames.Should().BeTrue();
                    reader.ReadNextFrame().Should().Equal(expectedFrame);
                }

                reader.HasMoreFrames.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void Write_With_NoFrames_Should_Produce_A_ReadableEmptyFile()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"tta_writer_empty_{Guid.NewGuid():N}.tta");
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    TtaWriter.Write(stream, channels: 1, bitsPerSample: 16, sampleRate: 44100, totalSamples: 0, []);
                }

                using var reader = TtaReader.Open(filePath);
                reader.TotalSamples.Should().Be(0);
                reader.HasMoreFrames.Should().BeFalse();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
