using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Mov;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.Mov
{
    public class Mp4SampleTableTest
    {
        // 0x60000000 (1,610,612,736): multiplied by any of this file's own per-entry byte widths
        // (4, 8, or 12) overflows int32 before Mp4SampleTable gets a chance to validate it -- the
        // untrusted raw count straight from the file, not a contrived value no real corruption could
        // produce (a single flipped high bit in an otherwise-plausible count field lands well within
        // this range).
        private const uint OverflowingCount = 0x60000000;

        [Fact]
        public void ReadSamples_WithStszDeclaringAnOverflowingSampleCount_Should_ThrowInvalidDataException()
        {
            var stsz = Box("stsz", new byte[4], UInt32Bytes(0), UInt32Bytes(OverflowingCount));
            var stsc = Box("stsc", new byte[4], UInt32Bytes(0));
            var stco = Box("stco", new byte[4], UInt32Bytes(0));
            var stbl = Box("stbl", stsz, stsc, stco);

            using var stream = new MemoryStream(stbl);
            var stblAtom = new MovAtom("stbl", 8, stbl.Length);

            var act = () => Mp4SampleTable.ReadSamples(stream, stblAtom);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ReadSamples_WithStscDeclaringAnOverflowingEntryCount_Should_ThrowInvalidDataException()
        {
            var stsz = Box("stsz", new byte[4], UInt32Bytes(0), UInt32Bytes(0));
            var stsc = Box("stsc", new byte[4], UInt32Bytes(OverflowingCount));
            var stco = Box("stco", new byte[4], UInt32Bytes(0));
            var stbl = Box("stbl", stsz, stsc, stco);

            using var stream = new MemoryStream(stbl);
            var stblAtom = new MovAtom("stbl", 8, stbl.Length);

            var act = () => Mp4SampleTable.ReadSamples(stream, stblAtom);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ReadSamples_WithStcoDeclaringAnOverflowingEntryCount_Should_ThrowInvalidDataException()
        {
            var stsz = Box("stsz", new byte[4], UInt32Bytes(0), UInt32Bytes(0));
            var stsc = Box("stsc", new byte[4], UInt32Bytes(0));
            var stco = Box("stco", new byte[4], UInt32Bytes(OverflowingCount));
            var stbl = Box("stbl", stsz, stsc, stco);

            using var stream = new MemoryStream(stbl);
            var stblAtom = new MovAtom("stbl", 8, stbl.Length);

            var act = () => Mp4SampleTable.ReadSamples(stream, stblAtom);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        [Fact]
        public void ReadSyncSamples_WithStssDeclaringAnOverflowingEntryCount_Should_ThrowInvalidDataException()
        {
            var stss = Box("stss", new byte[4], UInt32Bytes(OverflowingCount));
            var stbl = Box("stbl", stss);

            using var stream = new MemoryStream(stbl);
            var stblAtom = new MovAtom("stbl", 8, stbl.Length);

            var act = () => Mp4SampleTable.ReadSyncSamples(stream, stblAtom);

            act.Should().ThrowExactly<InvalidDataException>();
        }

        private static byte[] Box(string fourCc, params byte[][] parts)
        {
            var contentLength = 0;
            foreach (var part in parts)
            {
                contentLength += part.Length;
            }

            var box = new byte[8 + contentLength];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(box, 4);

            var offset = 8;
            foreach (var part in parts)
            {
                part.CopyTo(box, offset);
                offset += part.Length;
            }

            return box;
        }

        private static byte[] UInt32Bytes(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }
    }
}
