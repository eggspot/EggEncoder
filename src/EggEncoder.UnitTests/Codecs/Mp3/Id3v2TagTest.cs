using System.Text;
using EggEncoder.Codecs.Mp3;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mp3
{
    public class Id3v2TagTest
    {
        [Fact]
        public void BuildTagBytes_Then_ReadTags_Should_RoundTrip_TextFrames()
        {
            var tags = new Dictionary<string, string>
            {
                ["TIT2"] = "Test Title",
                ["TPE1"] = "Test Artist",
                ["TALB"] = "Test Album"
            };

            var tagBytes = Id3v2Writer.BuildTagBytes(tags);
            using var stream = new MemoryStream(tagBytes);

            var readTags = Id3v2Reader.ReadTags(stream);

            readTags.Should().NotBeNull();
            readTags!.Should().ContainKey("TIT2").WhoseValue.Should().Be("Test Title");
            readTags.Should().ContainKey("TPE1").WhoseValue.Should().Be("Test Artist");
            readTags.Should().ContainKey("TALB").WhoseValue.Should().Be("Test Album");
        }

        [Fact]
        public void BuildTagBytes_Should_RoundTrip_NonAsciiText_AsUtf8()
        {
            var tags = new Dictionary<string, string> { ["TIT2"] = "Fjæreland café é" };

            var tagBytes = Id3v2Writer.BuildTagBytes(tags);
            using var stream = new MemoryStream(tagBytes);

            var readTags = Id3v2Reader.ReadTags(stream);

            readTags!["TIT2"].Should().Be("Fjæreland café é");
        }

        [Theory]
        [InlineData("")]
        [InlineData("TI")]
        [InlineData("TIT2X")]
        [InlineData("AIT2")]
        [InlineData("TXXX")]
        public void BuildTagBytes_WithInvalidFrameId_Should_Throw(string invalidId)
        {
            var tags = new Dictionary<string, string> { [invalidId] = "value" };

            var act = () => Id3v2Writer.BuildTagBytes(tags);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ReadTags_WithNoId3v2Header_Should_ReturnNull()
        {
            var bytes = new byte[32];
            using var stream = new MemoryStream(bytes);

            var tags = Id3v2Reader.ReadTags(stream);

            tags.Should().BeNull();
        }

        [Fact]
        public void ReadTags_WithTooShortStream_Should_ReturnNull()
        {
            using var stream = new MemoryStream(new byte[4]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags.Should().BeNull();
        }

        [Fact]
        public void ReadTags_WithId3v2_2_MajorVersion_Should_ReturnEmpty_NotThrow()
        {
            // ID3v2.2 uses a different (3-character) frame id convention entirely -- a real but
            // out-of-scope version, same as AIFC's unsupported compressionTypes elsewhere in this
            // codebase. Should degrade gracefully, not throw.
            var header = BuildBareHeader(majorVersion: 2, flags: 0, tagSize: 100);
            using var stream = new MemoryStream([.. header, .. new byte[100]]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags.Should().NotBeNull();
            tags!.Should().BeEmpty();
        }

        [Fact]
        public void ReadTags_WithUnsynchronizationFlagSet_Should_ReturnEmpty_NotThrow()
        {
            var header = BuildBareHeader(majorVersion: 4, flags: 0x80, tagSize: 100);
            using var stream = new MemoryStream([.. header, .. new byte[100]]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags.Should().NotBeNull();
            tags!.Should().BeEmpty();
        }

        [Fact]
        public void ReadTags_WithExtendedHeaderFlagSet_Should_ReturnEmpty_NotThrow()
        {
            var header = BuildBareHeader(majorVersion: 4, flags: 0x40, tagSize: 100);
            using var stream = new MemoryStream([.. header, .. new byte[100]]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags.Should().NotBeNull();
            tags!.Should().BeEmpty();
        }

        [Fact]
        public void ReadTags_WithId3v2_3_PlainBigEndianFrameSize_Should_Decode()
        {
            // ID3v2.3's own frame-size field is plain big-endian, unlike the tag header's own
            // (always syncsafe) size field and unlike ID3v2.4's own syncsafe frame sizes.
            var textBytes = Encoding.Latin1.GetBytes("Hi");
            var frameData = new byte[] { 0 }.Concat(textBytes).ToArray(); // encoding 0 = Latin1

            var frame = new List<byte>();
            frame.AddRange(Encoding.ASCII.GetBytes("TIT2"));
            frame.AddRange([0, 0, 0, (byte)frameData.Length]); // plain big-endian size
            frame.AddRange([0, 0]); // flags
            frame.AddRange(frameData);

            var header = BuildBareHeader(majorVersion: 3, flags: 0, tagSize: frame.Count);
            using var stream = new MemoryStream([.. header, .. frame]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags!["TIT2"].Should().Be("Hi");
        }

        [Fact]
        public void ReadTags_WithNonZeroFrameFormatFlags_Should_SkipThatFrame_ButContinue()
        {
            var skippedFrameData = new byte[] { 0, (byte)'X' };
            var skippedFrame = new List<byte>();
            skippedFrame.AddRange(Encoding.ASCII.GetBytes("TPE1"));
            skippedFrame.AddRange(Id3v2WriterTestHelper.SyncSafe(skippedFrameData.Length));
            skippedFrame.AddRange([0, 0x08]); // compression flag set -- should skip decoding

            skippedFrame.AddRange(skippedFrameData);

            var keptFrameData = new byte[] { 0 }.Concat(Encoding.Latin1.GetBytes("Kept")).ToArray();
            var keptFrame = new List<byte>();
            keptFrame.AddRange(Encoding.ASCII.GetBytes("TIT2"));
            keptFrame.AddRange(Id3v2WriterTestHelper.SyncSafe(keptFrameData.Length));
            keptFrame.AddRange([0, 0]);
            keptFrame.AddRange(keptFrameData);

            var allFrames = skippedFrame.Concat(keptFrame).ToArray();
            var header = BuildBareHeader(majorVersion: 4, flags: 0, tagSize: allFrames.Length);
            using var stream = new MemoryStream([.. header, .. allFrames]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags!.Should().NotContainKey("TPE1");
            tags!["TIT2"].Should().Be("Kept");
        }

        [Fact]
        public void ReadTags_WithTxxxFrame_Should_Skip_NotDecode()
        {
            var frameData = new byte[] { 0, (byte)'x', (byte)'x' };
            var frame = new List<byte>();
            frame.AddRange(Encoding.ASCII.GetBytes("TXXX"));
            frame.AddRange(Id3v2WriterTestHelper.SyncSafe(frameData.Length));
            frame.AddRange([0, 0]);
            frame.AddRange(frameData);

            var header = BuildBareHeader(majorVersion: 4, flags: 0, tagSize: frame.Count);
            using var stream = new MemoryStream([.. header, .. frame]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags!.Should().BeEmpty();
        }

        [Fact]
        public void ReadTags_WithZeroPaddingAfterFrames_Should_StopCleanly()
        {
            var frameData = new byte[] { 0 }.Concat(Encoding.Latin1.GetBytes("Hi")).ToArray();
            var frame = new List<byte>();
            frame.AddRange(Encoding.ASCII.GetBytes("TIT2"));
            frame.AddRange(Id3v2WriterTestHelper.SyncSafe(frameData.Length));
            frame.AddRange([0, 0]);
            frame.AddRange(frameData);

            var padding = new byte[20];
            var tagSize = frame.Count + padding.Length;
            var header = BuildBareHeader(majorVersion: 4, flags: 0, tagSize: tagSize);
            using var stream = new MemoryStream([.. header, .. frame, .. padding]);

            var tags = Id3v2Reader.ReadTags(stream);

            tags!["TIT2"].Should().Be("Hi");
        }

        private static byte[] BuildBareHeader(byte majorVersion, byte flags, int tagSize)
        {
            var header = new byte[10];
            header[0] = (byte)'I';
            header[1] = (byte)'D';
            header[2] = (byte)'3';
            header[3] = majorVersion;
            header[4] = 0; // revision
            header[5] = flags;
            var sizeBytes = Id3v2WriterTestHelper.SyncSafe(tagSize);
            sizeBytes.CopyTo(header, 6);
            return header;
        }
    }

    internal static class Id3v2WriterTestHelper
    {
        public static byte[] SyncSafe(int value) =>
        [
            (byte)((value >> 21) & 0x7F),
            (byte)((value >> 14) & 0x7F),
            (byte)((value >> 7) & 0x7F),
            (byte)(value & 0x7F)
        ];
    }
}
