namespace EggEncoder.Containers.Matroska
{
    internal static class MatroskaLacing
    {
        private const int None = 0;
        private const int Xiph = 1;
        private const int Fixed = 2;

        public static IReadOnlyList<ReadOnlyMemory<byte>> Split(byte[] block, int offset, int lacing)
        {
            if (lacing == None)
            {
                return [block.AsMemory(offset)];
            }

            if (offset >= block.Length)
            {
                throw new InvalidDataException("Matroska: a laced block has no frame count");
            }

            var count = block[offset++] + 1;
            var sizes = new long[count];
            var known = 0L;
            switch (lacing)
            {
                case Xiph:
                    for (var i = 0; i < count - 1; i++)
                    {
                        long size = 0;
                        int value;
                        do
                        {
                            value = ByteAt(block, offset++);
                            size += value;
                        }
                        while (value == 255);

                        sizes[i] = size;
                        known += size;
                    }

                    break;
                case Fixed:
                    var remaining = block.Length - offset;
                    if (remaining % count != 0)
                    {
                        throw new InvalidDataException("Matroska: a fixed-size laced block doesn't divide evenly");
                    }

                    Array.Fill(sizes, remaining / count);
                    known = remaining - sizes[0];
                    break;
                default:
                    var first = ReadUnsigned(block, ref offset);
                    sizes[0] = first;
                    known = first;
                    var previous = first;
                    for (var i = 1; i < count - 1; i++)
                    {
                        previous += ReadSigned(block, ref offset);
                        if (previous < 0)
                        {
                            throw new InvalidDataException("Matroska: an EBML-laced frame size is negative");
                        }

                        sizes[i] = previous;
                        known += previous;
                    }

                    break;
            }

            var last = block.Length - offset - known;
            if (last < 0 || known < 0)
            {
                throw new InvalidDataException("Matroska: laced frame sizes run past the end of the block");
            }

            if (lacing != Fixed)
            {
                sizes[count - 1] = last;
            }

            var frames = new ReadOnlyMemory<byte>[count];
            for (var i = 0; i < count; i++)
            {
                frames[i] = block.AsMemory(offset, (int)sizes[i]);
                offset += (int)sizes[i];
            }

            return frames;
        }

        private static int ByteAt(byte[] block, int index) =>
            index < block.Length ? block[index] : throw new InvalidDataException("Matroska: a laced block's sizes are cut short");

        private static long ReadUnsigned(byte[] block, ref int offset)
        {
            var first = ByteAt(block, offset);
            if (first == 0)
            {
                throw new InvalidDataException("Matroska: an EBML lace size is invalid");
            }

            var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            long value = first & ((1 << (8 - length)) - 1);
            for (var i = 1; i < length; i++)
            {
                value = (value << 8) | (uint)ByteAt(block, offset + i);
            }

            offset += length;
            return value;
        }

        private static long ReadSigned(byte[] block, ref int offset)
        {
            var length = System.Numerics.BitOperations.LeadingZeroCount((uint)ByteAt(block, offset)) - 23;
            var value = ReadUnsigned(block, ref offset);
            return value - ((1L << ((7 * length) - 1)) - 1);
        }
    }
}
