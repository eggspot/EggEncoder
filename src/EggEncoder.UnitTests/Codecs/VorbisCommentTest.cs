using EggEncoder.Codecs;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs
{
    public class VorbisCommentTest
    {
        [Fact]
        public void BuildCommentListBytes_Then_ReadFrom_Should_RoundTrip_Tags()
        {
            var tags = new Dictionary<string, string>
            {
                ["TITLE"] = "Test Title",
                ["ARTIST"] = "Test Artist",
                ["ALBUM"] = "Test Album"
            };

            var bytes = VorbisCommentWriter.BuildCommentListBytes(tags);
            var readTags = VorbisCommentReader.ReadFrom(bytes, 0, bytes.Length);

            readTags.Should().ContainKey("TITLE").WhoseValue.Should().Be("Test Title");
            readTags.Should().ContainKey("ARTIST").WhoseValue.Should().Be("Test Artist");
            readTags.Should().ContainKey("ALBUM").WhoseValue.Should().Be("Test Album");
        }

        [Fact]
        public void BuildCommentListBytes_Should_RoundTrip_NonAsciiValue_AsUtf8()
        {
            var tags = new Dictionary<string, string> { ["TITLE"] = "Fjæreland café é" };

            var bytes = VorbisCommentWriter.BuildCommentListBytes(tags);
            var readTags = VorbisCommentReader.ReadFrom(bytes, 0, bytes.Length);

            readTags["TITLE"].Should().Be("Fjæreland café é");
        }

        [Fact]
        public void BuildCommentListBytes_WithEmptyTags_Should_Produce_ReadableEmptyResult()
        {
            var bytes = VorbisCommentWriter.BuildCommentListBytes(new Dictionary<string, string>());

            var readTags = VorbisCommentReader.ReadFrom(bytes, 0, bytes.Length);

            readTags.Should().BeEmpty();
        }

        [Theory]
        [InlineData("")]
        [InlineData("BAD=NAME")]
        [InlineData("BAD\u0001NAME")]
        public void BuildCommentListBytes_WithInvalidFieldName_Should_Throw(string invalidKey)
        {
            var tags = new Dictionary<string, string> { [invalidKey] = "value" };

            var act = () => VorbisCommentWriter.BuildCommentListBytes(tags);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ReadFrom_WithOffsetIntoLargerBuffer_Should_OnlyReadTheGivenRange()
        {
            var tags = new Dictionary<string, string> { ["TITLE"] = "Title" };
            var commentBytes = VorbisCommentWriter.BuildCommentListBytes(tags);

            var padding = new byte[] { 1, 2, 3 };
            byte[] buffer = [.. padding, .. commentBytes, .. padding];

            var readTags = VorbisCommentReader.ReadFrom(buffer, padding.Length, commentBytes.Length);

            readTags.Should().ContainKey("TITLE").WhoseValue.Should().Be("Title");
        }

        [Fact]
        public void ReadFrom_WithTruncatedVendorString_Should_ReturnEmpty_NotThrow()
        {
            var buffer = new byte[] { 100, 0, 0, 0 }; // claims a 100-byte vendor string but has none

            var readTags = VorbisCommentReader.ReadFrom(buffer, 0, buffer.Length);

            readTags.Should().BeEmpty();
        }

        [Fact]
        public void ReadFrom_WithTooShortForVendorLength_Should_ReturnEmpty_NotThrow()
        {
            var buffer = new byte[] { 0, 0 }; // not even 4 bytes for the vendor length field

            var readTags = VorbisCommentReader.ReadFrom(buffer, 0, buffer.Length);

            readTags.Should().BeEmpty();
        }

        [Fact]
        public void ReadFrom_WithCommentCountLargerThanActualData_Should_ReturnWhateverParsedCleanly()
        {
            var writer = new MemoryStream();
            WriteUInt32LittleEndian(writer, 0); // empty vendor string
            WriteUInt32LittleEndian(writer, 100); // claims 100 comments, but only one follows

            var firstComment = System.Text.Encoding.UTF8.GetBytes("TITLE=Only One");
            WriteUInt32LittleEndian(writer, (uint)firstComment.Length);
            writer.Write(firstComment);

            var buffer = writer.ToArray();
            var readTags = VorbisCommentReader.ReadFrom(buffer, 0, buffer.Length);

            readTags.Should().ContainKey("TITLE").WhoseValue.Should().Be("Only One");
            readTags.Should().HaveCount(1);
        }

        [Fact]
        public void ReadFrom_WithCommentMissingEqualsSign_Should_SkipThatEntry()
        {
            var writer = new MemoryStream();
            WriteUInt32LittleEndian(writer, 0); // empty vendor string
            WriteUInt32LittleEndian(writer, 1); // one comment

            var malformedComment = System.Text.Encoding.UTF8.GetBytes("NOEQUALSSIGN");
            WriteUInt32LittleEndian(writer, (uint)malformedComment.Length);
            writer.Write(malformedComment);

            var buffer = writer.ToArray();
            var readTags = VorbisCommentReader.ReadFrom(buffer, 0, buffer.Length);

            readTags.Should().BeEmpty();
        }

        [Fact]
        public void ReadFrom_WithEmptyBuffer_Should_ReturnEmpty_NotThrow()
        {
            var readTags = VorbisCommentReader.ReadFrom([], 0, 0);

            readTags.Should().BeEmpty();
        }

        [Fact]
        public void ReadFrom_WithDeclaredLengthLongerThanActualBuffer_Should_ReturnEmpty_NotThrow()
        {
            // A truncated FLAC file can declare a VORBIS_COMMENT block length that reaches past the
            // real end of the loaded file bytes -- every internal bounds check here only compares
            // against the caller-declared range, so without an explicit clamp to the buffer's own
            // actual length, this would throw IndexOutOfRangeException instead of degrading
            // gracefully like every other malformed-input case this type handles.
            var tags = new Dictionary<string, string> { ["TITLE"] = "Title" };
            var commentBytes = VorbisCommentWriter.BuildCommentListBytes(tags);
            var truncated = commentBytes[..(commentBytes.Length - 4)];

            var act = () => VorbisCommentReader.ReadFrom(truncated, 0, commentBytes.Length);

            act.Should().NotThrow();
            act().Should().BeEmpty();
        }

        [Fact]
        public void ReadFrom_WithOffsetPastEndOfBuffer_Should_ReturnEmpty_NotThrow()
        {
            var act = () => VorbisCommentReader.ReadFrom(new byte[4], offset: 100, length: 10);

            act.Should().NotThrow();
            act().Should().BeEmpty();
        }

        private static void WriteUInt32LittleEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
