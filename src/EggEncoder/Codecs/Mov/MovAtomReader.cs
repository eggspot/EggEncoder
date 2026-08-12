using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mov
{
    // Shared ISO base media (MOV/MP4) atom-tree walker used by both MovProbe (metadata-only) and
    // MovDecoder (audio decode) so the box-parsing logic isn't duplicated between them.
    internal static class MovAtomReader
    {
        public static MovAtom? FindAtom(Stream stream, string atomType, long rangeStart, long rangeEnd)
        {
            foreach (var atom in EnumerateAtoms(stream, rangeStart, rangeEnd))
            {
                if (atom.Type == atomType)
                {
                    return atom;
                }
            }

            return null;
        }

        public static List<MovAtom> EnumerateAtoms(Stream stream, long rangeStart, long rangeEnd)
        {
            var atoms = new List<MovAtom>();
            var position = rangeStart;
            var header = new byte[8];

            while (position + 8 <= rangeEnd)
            {
                stream.Position = position;
                stream.ReadExactly(header);

                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                var type = Encoding.ASCII.GetString(header, 4, 4);
                var headerSize = 8L;

                if (size == 1)
                {
                    var extendedSizeBuffer = new byte[8];
                    stream.ReadExactly(extendedSizeBuffer);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(extendedSizeBuffer);
                    headerSize = 16L;
                }
                else if (size == 0)
                {
                    size = rangeEnd - position;
                }

                atoms.Add(new MovAtom(type, position + headerSize, position + size));
                position += size;
            }

            return atoms;
        }
    }

    internal readonly record struct MovAtom(string Type, long ContentStart, long ContentEnd);
}
