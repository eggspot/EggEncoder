using System.Buffers.Binary;

namespace EggEncoder.Media
{
    /// <summary>
    /// Converts one H.264/H.265 access unit between length-prefixed framing (AVCC/HVCC: each NAL
    /// unit preceded by its big-endian byte length, used by FLV, RTMP, MP4 and fMP4) and Annex B
    /// framing (each NAL unit preceded by a <c>00 00 00 01</c> start code, used by MPEG-TS). Pure
    /// container framing: NAL unit contents are copied untouched, never parsed. Inserting an access
    /// unit delimiter or repeating SPS/PPS before an IDR is the MPEG-TS muxer's job, not this
    /// type's. See docs/live-ingest-restream-plan.md item 1.
    /// </summary>
    public static class AnnexBConverter
    {
        private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

        /// <summary>Length-prefixed → Annex B, a 4-byte start code before every NAL unit.</summary>
        /// <param name="lengthPrefixed">One access unit; must contain at least one NAL unit.</param>
        /// <param name="nalLengthSize">The prefix width in bytes: 1, 2 or 4 (<c>lengthSizeMinusOne + 1</c> from avcC/hvcC).</param>
        /// <exception cref="InvalidDataException">A length prefix is zero or runs past the end of the buffer, or there are no NAL units.</exception>
        public static byte[] ToAnnexB(ReadOnlySpan<byte> lengthPrefixed, int nalLengthSize = 4)
        {
            // Split (and validate) first, so the output is allocated exactly once.
            var nalUnits = SplitLengthPrefixed(lengthPrefixed, nalLengthSize);

            var outputLength = 0L;
            foreach (var (_, length) in nalUnits)
            {
                outputLength += StartCode.Length + length;
            }

            var output = new byte[outputLength];
            var writePosition = 0;
            foreach (var (offset, length) in nalUnits)
            {
                StartCode.CopyTo(output.AsSpan(writePosition));
                lengthPrefixed.Slice(offset, length).CopyTo(output.AsSpan(writePosition + StartCode.Length));
                writePosition += StartCode.Length + length;
            }

            return output;
        }

        /// <summary>
        /// The offset and length of every NAL unit (without its length prefix) in one
        /// length-prefixed access unit, validated the way <see cref="ToAnnexB"/> validates it.
        /// Shared with the MPEG-TS muxer, which interleaves its own NAL units with these.
        /// </summary>
        /// <exception cref="InvalidDataException">A length prefix is zero or runs past the end of the buffer, or there are no NAL units.</exception>
        internal static List<(int Offset, int Length)> SplitLengthPrefixed(ReadOnlySpan<byte> lengthPrefixed, int nalLengthSize)
        {
            ValidateNalLengthSize(nalLengthSize);

            var nalUnits = new List<(int, int)>();
            for (var position = 0; position < lengthPrefixed.Length;)
            {
                var nalLength = ReadNalLength(lengthPrefixed, position, nalLengthSize);
                nalUnits.Add((position + nalLengthSize, nalLength));
                position += nalLengthSize + nalLength;
            }

            if (nalUnits.Count == 0)
            {
                throw new InvalidDataException("Access unit contains no NAL units");
            }

            return nalUnits;
        }

        /// <summary>
        /// Annex B → length-prefixed. Accepts 3- and 4-byte start codes; zero bytes between a NAL
        /// unit and the next start code (<c>trailing_zero_8bits</c>) are dropped, since no NAL unit
        /// can end in a zero byte.
        /// </summary>
        /// <param name="annexB">One access unit, starting with a start code.</param>
        /// <param name="nalLengthSize">The prefix width to write: 1, 2 or 4.</param>
        /// <exception cref="InvalidDataException">The input doesn't start with a start code, contains no NAL units, or a NAL unit is too long for <paramref name="nalLengthSize"/>.</exception>
        public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB, int nalLengthSize = 4)
        {
            ValidateNalLengthSize(nalLengthSize);

            var nalUnits = SplitAnnexB(annexB);
            var maxNalLength = nalLengthSize == 4 ? int.MaxValue : (1 << (8 * nalLengthSize)) - 1;

            var outputLength = 0L;
            foreach (var (_, length) in nalUnits)
            {
                if (length > maxNalLength)
                {
                    throw new InvalidDataException($"NAL unit of {length} bytes does not fit a {nalLengthSize}-byte length prefix");
                }

                outputLength += nalLengthSize + length;
            }

            var output = new byte[outputLength];
            var writePosition = 0;
            foreach (var (offset, length) in nalUnits)
            {
                WriteNalLength(output.AsSpan(writePosition), length, nalLengthSize);
                annexB.Slice(offset, length).CopyTo(output.AsSpan(writePosition + nalLengthSize));
                writePosition += nalLengthSize + length;
            }

            return output;
        }

        private static List<(int Offset, int Length)> SplitAnnexB(ReadOnlySpan<byte> annexB)
        {
            var firstPayload = FindStartCode(annexB, 0, out var firstStartCodeLength);
            if (firstPayload != 0 && !IsAllZero(annexB[..firstPayload]))
            {
                throw new InvalidDataException("Annex B access unit does not start with a start code");
            }

            if (firstStartCodeLength == 0)
            {
                throw new InvalidDataException("Annex B access unit contains no start code");
            }

            var nalUnits = new List<(int, int)>();
            var nalStart = firstPayload + firstStartCodeLength;
            while (true)
            {
                var nextStartCode = FindStartCode(annexB, nalStart, out var nextStartCodeLength);
                var nalEnd = nextStartCodeLength == 0 ? annexB.Length : nextStartCode;

                // Drop trailing_zero_8bits (and the leading zero of a 4-byte start code).
                while (nalEnd > nalStart && annexB[nalEnd - 1] == 0)
                {
                    nalEnd--;
                }

                if (nalEnd > nalStart)
                {
                    nalUnits.Add((nalStart, nalEnd - nalStart));
                }

                if (nextStartCodeLength == 0)
                {
                    break;
                }

                nalStart = nextStartCode + nextStartCodeLength;
            }

            if (nalUnits.Count == 0)
            {
                throw new InvalidDataException("Annex B access unit contains no NAL units");
            }

            return nalUnits;
        }

        // Returns the index of the next 00 00 01 at or after `from`, reporting its length as 3
        // (the leading zero of a 4-byte start code is stripped by the caller's trailing-zero
        // trim), or startCodeLength 0 if there is none.
        private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int startCodeLength)
        {
            for (var i = from; i + 2 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    startCodeLength = 3;
                    return i;
                }
            }

            startCodeLength = 0;
            return data.Length;
        }

        private static bool IsAllZero(ReadOnlySpan<byte> data) => data.IndexOfAnyExcept((byte)0) < 0;

        private static int ReadNalLength(ReadOnlySpan<byte> data, int position, int nalLengthSize)
        {
            if (data.Length - position < nalLengthSize)
            {
                throw new InvalidDataException($"Truncated {nalLengthSize}-byte NAL length prefix at offset {position}");
            }

            var prefix = data.Slice(position, nalLengthSize);
            long nalLength = nalLengthSize switch
            {
                1 => prefix[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(prefix),
                _ => BinaryPrimitives.ReadUInt32BigEndian(prefix)
            };

            if (nalLength == 0)
            {
                throw new InvalidDataException($"Zero-length NAL unit at offset {position}");
            }

            if (nalLength > data.Length - position - nalLengthSize)
            {
                throw new InvalidDataException($"NAL unit length {nalLength} at offset {position} runs past the end of the access unit");
            }

            return (int)nalLength;
        }

        private static void WriteNalLength(Span<byte> destination, int length, int nalLengthSize)
        {
            switch (nalLengthSize)
            {
                case 1:
                    destination[0] = (byte)length;
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)length);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)length);
                    break;
            }
        }

        private static void ValidateNalLengthSize(int nalLengthSize)
        {
            if (nalLengthSize is not (1 or 2 or 4))
            {
                throw new ArgumentOutOfRangeException(nameof(nalLengthSize), nalLengthSize, "NAL length size must be 1, 2 or 4 bytes");
            }
        }
    }
}
