using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mov
{
    internal static class Mp4TrackReader
    {
        public const int VisualSampleEntryFieldBytes = 78;
        public const int AudioSampleEntryFieldBytes = 28;

        public static MovAtom FindMoov(Stream stream, string sourceName) =>
            MovAtomReader.FindAtom(stream, "moov", 0, stream.Length)
                ?? throw new InvalidDataException($"'{sourceName}' is not a valid MOV/MP4 file: missing 'moov' atom");

        public static bool IsFragmented(Stream stream, MovAtom moov) =>
            MovAtomReader.FindAtom(stream, "mvex", moov.ContentStart, moov.ContentEnd) is not null;

        public static (uint Timescale, ulong Duration) ReadMovieHeader(Stream stream, MovAtom moov)
        {
            var mvhd = MovAtomReader.FindAtom(stream, "mvhd", moov.ContentStart, moov.ContentEnd)
                ?? throw new InvalidDataException("MOV/MP4 file is missing an 'mvhd' atom");
            var content = ReadContent(stream, mvhd, 32);
            return content[0] == 1
                ? (BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(20, 4)), BinaryPrimitives.ReadUInt64BigEndian(content.AsSpan(24, 8)))
                : (BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(12, 4)), BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(16, 4)));
        }

        public static List<Mp4TrackHeader> ReadTrackHeaders(Stream stream, MovAtom moov)
        {
            var headers = new List<Mp4TrackHeader>();
            foreach (var trak in MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd))
            {
                if (trak.Type == "trak" && TryReadTrackHeader(stream, trak) is { } header)
                {
                    headers.Add(header);
                }
            }

            return headers;
        }

        public static Mp4TrackHeader? TryReadTrackHeader(Stream stream, MovAtom trak)
        {
            var tkhd = MovAtomReader.FindAtom(stream, "tkhd", trak.ContentStart, trak.ContentEnd);
            var mdia = MovAtomReader.FindAtom(stream, "mdia", trak.ContentStart, trak.ContentEnd);
            if (tkhd is null || mdia is null)
            {
                return null;
            }

            var mdhd = MovAtomReader.FindAtom(stream, "mdhd", mdia.Value.ContentStart, mdia.Value.ContentEnd);
            var hdlr = MovAtomReader.FindAtom(stream, "hdlr", mdia.Value.ContentStart, mdia.Value.ContentEnd);
            var minf = MovAtomReader.FindAtom(stream, "minf", mdia.Value.ContentStart, mdia.Value.ContentEnd);
            var stbl = minf is null ? null : MovAtomReader.FindAtom(stream, "stbl", minf.Value.ContentStart, minf.Value.ContentEnd);
            if (mdhd is null || hdlr is null || stbl is null)
            {
                return null;
            }

            var tkhdContent = ReadContent(stream, tkhd.Value, 84);
            var isVersion1 = tkhdContent[0] == 1;
            if (isVersion1)
            {
                tkhdContent = ReadContent(stream, tkhd.Value, 96);
            }

            var trackId = BinaryPrimitives.ReadInt32BigEndian(tkhdContent.AsSpan(isVersion1 ? 20 : 12, 4));
            var matrixOffset = isVersion1 ? 52 : 40;
            var a = BinaryPrimitives.ReadInt32BigEndian(tkhdContent.AsSpan(matrixOffset, 4));
            var b = BinaryPrimitives.ReadInt32BigEndian(tkhdContent.AsSpan(matrixOffset + 4, 4));
            var sizeOffset = isVersion1 ? 88 : 76;
            var width = BinaryPrimitives.ReadInt32BigEndian(tkhdContent.AsSpan(sizeOffset, 4)) >> 16;
            var height = BinaryPrimitives.ReadInt32BigEndian(tkhdContent.AsSpan(sizeOffset + 4, 4)) >> 16;

            var mdhdContent = ReadContent(stream, mdhd.Value, 24);
            var timescale = mdhdContent[0] == 1
                ? BinaryPrimitives.ReadUInt32BigEndian(ReadContent(stream, mdhd.Value, 32).AsSpan(20, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(mdhdContent.AsSpan(12, 4));

            var handler = Encoding.ASCII.GetString(ReadContent(stream, hdlr.Value, 12).AsSpan(8, 4));

            return new Mp4TrackHeader(trak, stbl.Value, Math.Max(trackId, 0), handler, (int)Math.Min(timescale, int.MaxValue), RotationFromMatrix(a, b), Math.Max(width, 0), Math.Max(height, 0));
        }

        public static int RotationFromMatrix(int a, int b)
        {
            if (a == 0 && b == 0)
            {
                return 0;
            }

            var degrees = Math.Atan2(b, a) * 180 / Math.PI;
            var quarterTurns = (int)Math.Round(degrees / 90);
            return ((quarterTurns % 4) + 4) % 4 * 90;
        }

        public static Mp4SampleEntry ReadFirstSampleEntry(Stream stream, MovAtom stbl)
        {
            var stsd = MovAtomReader.FindAtom(stream, "stsd", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException("Track is missing an 'stsd' atom");
            var header = ReadContent(stream, stsd, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) == 0)
            {
                throw new InvalidDataException("'stsd' has no sample description entries");
            }

            var entryStart = stsd.ContentStart + 8;
            var entrySize = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
            var fourCc = Encoding.ASCII.GetString(header, 12, 4);
            var entryEnd = Math.Min(entryStart + entrySize, stsd.ContentEnd);
            if (entrySize < 8 || entryEnd <= entryStart + 8)
            {
                throw new InvalidDataException($"'stsd' sample entry '{fourCc}' has an invalid size {entrySize}");
            }

            return new Mp4SampleEntry(fourCc, entryStart + 8, entryEnd);
        }

        public static Mp4SampleTiming ReadTiming(Stream stream, MovAtom stbl, int sampleCount)
        {
            var stts = MovAtomReader.FindAtom(stream, "stts", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException("Missing 'stts' atom in sample table");
            var decodeTimes = new long[sampleCount];
            var durations = new long[sampleCount];
            var entries = ReadEntries(stream, stts, "'stts'");
            var index = 0;
            var time = 0L;
            for (var e = 0; e < entries.Length / 8 && index < sampleCount; e++)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(e * 8, 4));
                long delta = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan((e * 8) + 4, 4));
                for (var i = 0u; i < count && index < sampleCount; i++)
                {
                    decodeTimes[index] = time;
                    durations[index] = delta;
                    time += delta;
                    index++;
                }
            }

            if (index < sampleCount)
            {
                throw new InvalidDataException($"'stts' covers {index} samples, but the track has {sampleCount}");
            }

            int[]? compositionOffsets = null;
            var ctts = MovAtomReader.FindAtom(stream, "ctts", stbl.ContentStart, stbl.ContentEnd);
            if (ctts is not null)
            {
                compositionOffsets = new int[sampleCount];
                var cttsEntries = ReadEntries(stream, ctts.Value, "'ctts'");
                index = 0;
                for (var e = 0; e < cttsEntries.Length / 8 && index < sampleCount; e++)
                {
                    var count = BinaryPrimitives.ReadUInt32BigEndian(cttsEntries.AsSpan(e * 8, 4));
                    var offset = BinaryPrimitives.ReadInt32BigEndian(cttsEntries.AsSpan((e * 8) + 4, 4));
                    for (var i = 0u; i < count && index < sampleCount; i++)
                    {
                        compositionOffsets[index++] = offset;
                    }
                }
            }

            return new Mp4SampleTiming(decodeTimes, durations, compositionOffsets, time);
        }

        public static Mp4Edit ReadEdit(Stream stream, MovAtom trak, uint movieTimescale, int mediaTimescale)
        {
            var edts = MovAtomReader.FindAtom(stream, "edts", trak.ContentStart, trak.ContentEnd);
            var elst = edts is null ? null : MovAtomReader.FindAtom(stream, "elst", edts.Value.ContentStart, edts.Value.ContentEnd);
            if (elst is null)
            {
                return new Mp4Edit(0, null);
            }

            var version = ReadContent(stream, elst.Value, 4)[0];
            var entryBytes = version == 1 ? 20 : 12;
            stream.Position = elst.Value.ContentStart + 4;
            Span<byte> countBuffer = stackalloc byte[4];
            stream.ReadExactly(countBuffer);
            var count = Mp4SampleTable.CheckedEntryCount(BinaryPrimitives.ReadUInt32BigEndian(countBuffer), entryBytes, "'elst'");
            if (elst.Value.ContentStart + 8 + ((long)count * entryBytes) > elst.Value.ContentEnd)
            {
                throw new InvalidDataException("'elst' declares more entries than it holds");
            }

            var buffer = new byte[count * entryBytes];
            stream.ReadExactly(buffer);
            var emptyTicks = 0L;
            for (var i = 0; i < count; i++)
            {
                var entry = buffer.AsSpan(i * entryBytes, entryBytes);
                var segmentDuration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry[..8]) : BinaryPrimitives.ReadUInt32BigEndian(entry[..4]);
                var mediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(entry[8..16]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..8]);
                if (mediaTime == -1)
                {
                    if (movieTimescale > 0)
                    {
                        emptyTicks = CheckedAdd(emptyTicks, () => Media.MediaTimestamp.Rescale(segmentDuration, (int)Math.Min(movieTimescale, int.MaxValue), mediaTimescale));
                    }

                    continue;
                }

                var shift = CheckedAdd(emptyTicks, () => checked(-mediaTime));
                var isOnlyMediaEdit = true;
                for (var j = i + 1; j < count; j++)
                {
                    var later = buffer.AsSpan(j * entryBytes, entryBytes);
                    var laterMediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(later[8..16]) : BinaryPrimitives.ReadInt32BigEndian(later[4..8]);
                    isOnlyMediaEdit &= laterMediaTime == -1;
                }

                long? end = isOnlyMediaEdit && movieTimescale > 0 && segmentDuration > 0
                    ? CheckedAdd(emptyTicks, () => Media.MediaTimestamp.Rescale(segmentDuration, (int)Math.Min(movieTimescale, int.MaxValue), mediaTimescale))
                    : null;
                return new Mp4Edit(shift, end);
            }

            return new Mp4Edit(emptyTicks, null);
        }

        private static long CheckedAdd(long value, Func<long> term)
        {
            try
            {
                return checked(value + term());
            }
            catch (OverflowException e)
            {
                throw new InvalidDataException("'elst' times don't fit a 64-bit timestamp", e);
            }
        }

        private static byte[] ReadEntries(Stream stream, MovAtom atom, string atomName)
        {
            stream.Position = atom.ContentStart + 4;
            Span<byte> countBuffer = stackalloc byte[4];
            stream.ReadExactly(countBuffer);
            var count = Mp4SampleTable.CheckedEntryCount(BinaryPrimitives.ReadUInt32BigEndian(countBuffer), 8, atomName);
            if (atom.ContentStart + 8 + ((long)count * 8) > atom.ContentEnd)
            {
                throw new InvalidDataException($"{atomName} declares more entries than it holds");
            }

            var buffer = new byte[count * 8];
            stream.ReadExactly(buffer);
            return buffer;
        }

        public static byte[] ReadContent(Stream stream, MovAtom atom, int length)
        {
            if (atom.ContentEnd - atom.ContentStart < length)
            {
                throw new InvalidDataException($"'{atom.Type}' atom is too short: {atom.ContentEnd - atom.ContentStart} bytes, at least {length} needed");
            }

            stream.Position = atom.ContentStart;
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        public static byte[] ReadWholeContent(Stream stream, MovAtom atom)
        {
            var length = atom.ContentEnd - atom.ContentStart;
            if (length is < 0 or > 16 * 1024 * 1024)
            {
                throw new InvalidDataException($"'{atom.Type}' atom has an invalid size {length}");
            }

            return ReadContent(stream, atom, (int)length);
        }
    }

    internal sealed record Mp4TrackHeader(MovAtom Trak, MovAtom Stbl, int TrackId, string HandlerType, int Timescale, int Rotation, int Width, int Height);

    internal readonly record struct Mp4Edit(long Shift, long? End);

    internal readonly record struct Mp4SampleEntry(string FourCc, long FieldsStart, long End);

    internal sealed record Mp4SampleTiming(long[] DecodeTimes, long[] Durations, int[]? CompositionOffsets, long TotalDuration);
}
