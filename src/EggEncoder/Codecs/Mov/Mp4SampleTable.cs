using System.Buffers.Binary;

namespace EggEncoder.Codecs.Mov
{
    // Resolves an ISO base media 'stbl' sample table (stsz + stsc + stco/co64) into the absolute
    // file offset and byte length of every sample, in order. This is standard ISO/IEC 14496-12
    // chunk/sample bookkeeping -- stsz gives each sample's size, stco/co64 gives each chunk's file
    // offset, and stsc maps chunk ranges to how many samples each of those chunks holds.
    internal static class Mp4SampleTable
    {
        public static List<(long Offset, int Size)> ReadSamples(Stream stream, MovAtom stbl)
        {
            var stsz = MovAtomReader.FindAtom(stream, "stsz", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException("Missing 'stsz' atom in sample table");
            var stsc = MovAtomReader.FindAtom(stream, "stsc", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException("Missing 'stsc' atom in sample table");
            var stco = MovAtomReader.FindAtom(stream, "stco", stbl.ContentStart, stbl.ContentEnd);
            var co64 = stco is null ? MovAtomReader.FindAtom(stream, "co64", stbl.ContentStart, stbl.ContentEnd) : null;

            if (stco is null && co64 is null)
            {
                throw new InvalidDataException("Missing 'stco'/'co64' atom in sample table");
            }

            var sampleSizes = ReadStsz(stream, stsz);
            var chunkOffsets = stco is not null ? ReadChunkOffsets(stream, stco.Value, is64Bit: false) : ReadChunkOffsets(stream, co64!.Value, is64Bit: true);
            var chunkEntries = ReadStsc(stream, stsc);

            var samples = new List<(long Offset, int Size)>(sampleSizes.Count);
            var sampleIndex = 0;

            for (var chunkIndex = 0; chunkIndex < chunkOffsets.Count && sampleIndex < sampleSizes.Count; chunkIndex++)
            {
                var samplesInChunk = SamplesPerChunk(chunkEntries, chunkIndex + 1);
                var offsetInChunk = chunkOffsets[chunkIndex];

                for (var i = 0; i < samplesInChunk && sampleIndex < sampleSizes.Count; i++)
                {
                    var size = sampleSizes[sampleIndex];
                    samples.Add((offsetInChunk, size));
                    offsetInChunk += size;
                    sampleIndex++;
                }
            }

            return samples;
        }

        // Reads the 'stbl' sample table's optional 'stss' (sync sample) box: a list of 1-indexed
        // sample numbers that are random-access points (keyframes, for a video track). Returns a
        // 0-indexed set for direct lookup against ReadSamples's own 0-indexed sample list, or null
        // if no 'stss' box is present -- per ISO/IEC 14496-12, that absence specifically means
        // every sample is a sync sample (there is no ambiguity to resolve here; a track with no
        // 'stss' box has no non-random-access samples at all). An 'stss' box that IS present but
        // has zero entries is a different, valid state (no sample is ever a sync sample) and is
        // returned as a non-null, empty set, not folded into the "absent" case.
        public static HashSet<int>? ReadSyncSamples(Stream stream, MovAtom stbl)
        {
            var stss = MovAtomReader.FindAtom(stream, "stss", stbl.ContentStart, stbl.ContentEnd);
            if (stss is null)
            {
                return null;
            }

            stream.Position = stss.Value.ContentStart + 4; // version+flags
            Span<byte> countBuffer = stackalloc byte[4];
            stream.ReadExactly(countBuffer);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(countBuffer);

            var buffer = new byte[count * 4];
            stream.ReadExactly(buffer);

            var syncSamples = new HashSet<int>(count);
            for (var i = 0; i < count; i++)
            {
                var oneIndexedSampleNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 4, 4));
                syncSamples.Add(oneIndexedSampleNumber - 1);
            }

            return syncSamples;
        }

        private static int SamplesPerChunk(List<(int FirstChunk, int SamplesPerChunk, int SampleDescriptionIndex)> entries, int chunkNumber)
        {
            var samplesPerChunk = entries[0].SamplesPerChunk;

            foreach (var entry in entries)
            {
                if (entry.FirstChunk > chunkNumber)
                {
                    break;
                }

                samplesPerChunk = entry.SamplesPerChunk;
            }

            return samplesPerChunk;
        }

        private static List<int> ReadStsz(Stream stream, MovAtom stsz)
        {
            stream.Position = stsz.ContentStart + 4;
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);

            var uniformSampleSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var sampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);

            var sizes = new List<int>(sampleCount);

            if (uniformSampleSize != 0)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    sizes.Add((int)uniformSampleSize);
                }

                return sizes;
            }

            var buffer = new byte[sampleCount * 4];
            stream.ReadExactly(buffer);

            for (var i = 0; i < sampleCount; i++)
            {
                sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 4, 4)));
            }

            return sizes;
        }

        private static List<long> ReadChunkOffsets(Stream stream, MovAtom chunkOffsetAtom, bool is64Bit)
        {
            stream.Position = chunkOffsetAtom.ContentStart + 4;
            Span<byte> countBuffer = stackalloc byte[4];
            stream.ReadExactly(countBuffer);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(countBuffer);

            var entrySize = is64Bit ? 8 : 4;
            var buffer = new byte[count * entrySize];
            stream.ReadExactly(buffer);

            var offsets = new List<long>(count);
            for (var i = 0; i < count; i++)
            {
                offsets.Add(is64Bit
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(i * entrySize, 8))
                    : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * entrySize, 4)));
            }

            return offsets;
        }

        private static List<(int FirstChunk, int SamplesPerChunk, int SampleDescriptionIndex)> ReadStsc(Stream stream, MovAtom stsc)
        {
            stream.Position = stsc.ContentStart + 4;
            Span<byte> countBuffer = stackalloc byte[4];
            stream.ReadExactly(countBuffer);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(countBuffer);

            var buffer = new byte[count * 12];
            stream.ReadExactly(buffer);

            var entries = new List<(int, int, int)>(count);
            for (var i = 0; i < count; i++)
            {
                var firstChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 12, 4));
                var samplesPerChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan((i * 12) + 4, 4));
                var sampleDescriptionIndex = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan((i * 12) + 8, 4));
                entries.Add((firstChunk, samplesPerChunk, sampleDescriptionIndex));
            }

            return entries;
        }
    }
}
