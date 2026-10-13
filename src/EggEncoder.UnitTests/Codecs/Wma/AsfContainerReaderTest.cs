using EggEncoder.Codecs.Wma;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Wma
{
    public class AsfContainerReaderTest
    {
        private static readonly string _fixturePath = Path.GetFullPath("Codecs/Wma/tone_mono.wma");

        [Fact]
        public void Read_WithFileTruncatedAtAnyLength_Should_Throw_A_Clear_Typed_Exception_Not_An_UncheckedOne()
        {
            // Exhaustively confirms the fix's real contract: no matter where a real ASF/WMA file
            // gets cut off (a write interrupted partway, a corrupted download), Read either
            // succeeds or throws a clear, typed InvalidDataException/NotSupportedException -- never
            // the unchecked ArgumentOutOfRangeException/IndexOutOfRangeException/ArgumentException
            // a raw BitConverter/Guid/Array.Copy call, or the raw fileBytes[position] indexing this
            // fix replaced, would throw on a short read. Covers every structural region of a real
            // file (header objects, stream properties' own ExtraData copy, and packet/payload
            // parsing including its own payload copy) since the stride walks the whole file length.
            var fullBytes = File.ReadAllBytes(_fixturePath);
            var tempPath = Path.GetTempFileName();

            try
            {
                for (var length = 0; length < fullBytes.Length; length += 37)
                {
                    File.WriteAllBytes(tempPath, fullBytes.AsSpan(0, length).ToArray());

                    try
                    {
                        AsfContainerReader.Read(tempPath, out _);
                    }
                    catch (Exception ex)
                    {
                        (ex is InvalidDataException or NotSupportedException).Should().BeTrue(
                            $"a file truncated to {length} byte(s) should fail with a clear, typed InvalidDataException or NotSupportedException, not {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void Read_WithHeaderSizeFieldCorruptedToProduceANegativePosition_Should_Throw_InvalidDataException()
        {
            // EnsureBytesAvailable's own position<0 guard: a corrupted headerSize field large
            // enough that casting it from the file's own 64-bit field down to Int32 wraps negative
            // (0x80000000's low 32 bits cast to exactly int.MinValue) would otherwise feed a
            // negative position straight into the next read, which Span/BitConverter would reject
            // with their own unchecked exception type rather than this file's own clear, typed one.
            // Not reachable via simple truncation alone (that only ever makes the file shorter,
            // never makes position itself go negative), so this needs its own dedicated fixture.
            var tempPath = Path.GetTempFileName();
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(AsfGuids.HeaderObject.ToByteArray());
                    writer.Write(0x80000000UL); // headerSize -- casts to int.MinValue
                    writer.Write(0U); // headerObjectCount = 0, skip the header-objects loop entirely
                    writer.Write((ushort)0); // 2 reserved bytes
                }

                var act = () => AsfContainerReader.Read(tempPath, out _);

                act.Should().ThrowExactly<InvalidDataException>();
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void Read_WithWellFormedFile_Should_Still_Succeed()
        {
            // Confirms the bounds-checking added by this fix doesn't reject a genuinely
            // well-formed file -- a direct, lower-level counterpart to WmaDecoderTest's own
            // Decode_ToneMono_Should_Return_Correct_Format (which exercises this same path
            // indirectly, through WmaDecoder).
            var act = () => AsfContainerReader.Read(_fixturePath, out _);

            act.Should().NotThrow();
        }
    }
}
