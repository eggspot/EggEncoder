using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mov
{
    // Minimal big-endian ISO base media (ISOBMFF) box-content builder -- the write-side counterpart
    // to MovAtomReader's own read-side box-tree walk, kept alongside it since both are low-level
    // ISOBMFF plumbing. WriteBox wraps accumulated content with the standard 8-byte [uint32 size]
    // [4-byte ASCII type] box header, the exact layout MovAtomReader.EnumerateAtoms already parses
    // (no 64-bit extended-size variant, since no box either init or media fMP4 segment ever writes
    // is anywhere close to 4GB). Used by Streaming.Mp4's own fMP4/CMAF segment writers.
    internal sealed class Mp4BoxBuilder
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public Mp4BoxBuilder WriteUInt8(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        public Mp4BoxBuilder WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
            return this;
        }

        public Mp4BoxBuilder WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

        public Mp4BoxBuilder WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
            return this;
        }

        public Mp4BoxBuilder WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

        public Mp4BoxBuilder WriteUInt64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
            return this;
        }

        public Mp4BoxBuilder WriteFourCc(string fourCc)
        {
            if (fourCc.Length != 4)
            {
                throw new ArgumentException("A box type/FourCC must be exactly 4 characters.", nameof(fourCc));
            }

            _bytes.AddRange(Encoding.ASCII.GetBytes(fourCc));
            return this;
        }

        public Mp4BoxBuilder WriteBytes(byte[] raw)
        {
            _bytes.AddRange(raw);
            return this;
        }

        /// <summary>
        /// The standard ISOBMFF unity transformation matrix (<c>mvhd</c>/<c>tkhd</c>'s own 9-entry,
        /// 16.16-fixed-point field): {1,0,0, 0,1,0, 0,0,16384} -- 36 bytes.
        /// </summary>
        public Mp4BoxBuilder WriteIdentityMatrix()
        {
            const uint unity = 0x00010000;
            const uint unityW = 0x40000000;

            return WriteUInt32(unity).WriteUInt32(0).WriteUInt32(0)
                .WriteUInt32(0).WriteUInt32(unity).WriteUInt32(0)
                .WriteUInt32(0).WriteUInt32(0).WriteUInt32(unityW);
        }

        /// <summary>Appends a complete box: an 8-byte header (size + FourCC) followed by <paramref name="content"/>.</summary>
        public Mp4BoxBuilder WriteBox(string fourCc, byte[] content)
        {
            WriteUInt32((uint)(8 + content.Length));
            WriteFourCc(fourCc);
            WriteBytes(content);
            return this;
        }

        /// <summary>Appends a complete box whose content is itself built by a nested <see cref="Mp4BoxBuilder"/>.</summary>
        public Mp4BoxBuilder WriteBox(string fourCc, Action<Mp4BoxBuilder> buildContent)
        {
            var inner = new Mp4BoxBuilder();
            buildContent(inner);
            return WriteBox(fourCc, inner.ToArray());
        }

        /// <summary>Overwrites 4 bytes at <paramref name="byteOffset"/> with <paramref name="value"/>, for a field (e.g. <c>trun</c>'s <c>data_offset</c>) only known once the rest of the box has been built.</summary>
        public void PatchUInt32(int byteOffset, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            for (var i = 0; i < 4; i++)
            {
                _bytes[byteOffset + i] = buffer[i];
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
