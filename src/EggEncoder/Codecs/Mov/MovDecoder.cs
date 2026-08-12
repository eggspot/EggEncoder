using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Aac;
using EggEncoder.Transform;

namespace EggEncoder.Codecs.Mov
{
    // Decodes the AAC audio track of a MOV/MP4 file. MP4 stores AAC as raw access units (one per
    // sample-table entry, no ADTS framing) described by the 'esds' box's AudioSpecificConfig, so
    // this locates the audio track, resolves its sample table to (offset, size) pairs, and feeds
    // each sample's raw bytes through AacFrameDecoder -- the same engine standalone .aac decoding
    // uses, just without ADTS headers to parse. Mono AAC-LC only, matching this library's AAC
    // support everywhere else; fragmented MP4 (moof/mvex) and multiple sample description table
    // entries are not handled.
    public static class MovDecoder
    {
        private const int SamplesPerAacFrame = 1024;

        public static MovStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            var moov = MovAtomReader.FindAtom(stream, "moov", 0, stream.Length)
                ?? throw new InvalidDataException($"'{filePath}' is not a valid MOV/MP4 file: missing 'moov' atom");

            var audioTrak = FindAudioTrack(stream, moov)
                ?? throw new InvalidDataException($"'{filePath}' does not contain an audio track");

            var mdia = MovAtomReader.FindAtom(stream, "mdia", audioTrak.ContentStart, audioTrak.ContentEnd)!.Value;
            var minf = MovAtomReader.FindAtom(stream, "minf", mdia.ContentStart, mdia.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': audio track is missing an 'minf' atom");
            var stbl = MovAtomReader.FindAtom(stream, "stbl", minf.ContentStart, minf.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': audio track is missing an 'stbl' atom");
            var stsd = MovAtomReader.FindAtom(stream, "stsd", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': audio track is missing an 'stsd' atom");

            var audioConfig = ReadAudioConfig(stream, stsd, filePath);

            if (audioConfig.Channels != 1)
            {
                throw new NotSupportedException("Only mono AAC-in-MP4 tracks are supported");
            }

            var samples = Mp4SampleTable.ReadSamples(stream, stbl);
            var totalSamplesPerChannel = (long)samples.Count * SamplesPerAacFrame;

            var frameDecoder = new AacFrameDecoder();
            var sampleBuffer = Array.Empty<byte>();

            foreach (var (offset, size) in samples)
            {
                if (sampleBuffer.Length < size)
                {
                    sampleBuffer = new byte[size];
                }

                stream.Position = offset;
                stream.ReadExactly(sampleBuffer.AsSpan(0, size));

                var reader = new BitReader(sampleBuffer, 0, size);
                var outputFrame = frameDecoder.DecodeFrame(reader, audioConfig.Channels);

                onBlockDecoded(outputFrame, audioConfig.Channels, audioConfig.SampleRate, 16, totalSamplesPerChannel);
            }

            return new MovStreamInfo
            {
                Channels = audioConfig.Channels,
                SampleRate = audioConfig.SampleRate,
                BitsPerSample = 16,
                TotalSamples = totalSamplesPerChannel
            };
        }

        private static MovAtom? FindAudioTrack(Stream stream, MovAtom moov)
        {
            foreach (var trak in MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd))
            {
                if (trak.Type != "trak")
                {
                    continue;
                }

                var mdia = MovAtomReader.FindAtom(stream, "mdia", trak.ContentStart, trak.ContentEnd);
                var hdlr = mdia is null ? null : MovAtomReader.FindAtom(stream, "hdlr", mdia.Value.ContentStart, mdia.Value.ContentEnd);
                if (hdlr is null)
                {
                    continue;
                }

                if (ReadHandlerComponentType(stream, hdlr.Value) == "soun")
                {
                    return trak;
                }
            }

            return null;
        }

        private static string ReadHandlerComponentType(Stream stream, MovAtom hdlr)
        {
            stream.Position = hdlr.ContentStart + 8; // version+flags(4) + pre_defined(4)
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return Encoding.ASCII.GetString(buffer);
        }

        private static Mp4AudioConfig ReadAudioConfig(Stream stream, MovAtom stsd, string filePath)
        {
            stream.Position = stsd.ContentStart + 4; // version+flags
            Span<byte> entryCountBuffer = stackalloc byte[4];
            stream.ReadExactly(entryCountBuffer);
            var entryCount = BinaryPrimitives.ReadUInt32BigEndian(entryCountBuffer);

            if (entryCount == 0)
            {
                throw new InvalidDataException($"'{filePath}': 'stsd' has no sample description entries");
            }

            var sampleEntryStart = stream.Position;
            Span<byte> sampleEntryHeader = stackalloc byte[8];
            stream.ReadExactly(sampleEntryHeader);
            var sampleEntrySize = (long)BinaryPrimitives.ReadUInt32BigEndian(sampleEntryHeader[..4]);
            var codecFourCc = Encoding.ASCII.GetString(sampleEntryHeader[4..8]);

            if (codecFourCc != "mp4a")
            {
                throw new NotSupportedException($"MP4 audio codec '{codecFourCc}' is not supported; only AAC ('mp4a') is supported");
            }

            // AudioSampleEntry fixed fields after the 8-byte box header: reserved(6) +
            // data_reference_index(2) + version/revision/vendor(8) + channelcount(2) +
            // samplesize(2) + pre_defined(2) + reserved(2) + samplerate(4) = 28 bytes.
            var audioSampleEntryFixedFieldsEnd = sampleEntryStart + 8 + 28;
            var esds = MovAtomReader.FindAtom(stream, "esds", audioSampleEntryFixedFieldsEnd, sampleEntryStart + sampleEntrySize)
                ?? throw new InvalidDataException($"'{filePath}': 'mp4a' sample entry is missing an 'esds' atom");

            stream.Position = esds.ContentStart;
            var esdsBytes = new byte[esds.ContentEnd - esds.ContentStart];
            stream.ReadExactly(esdsBytes);

            return Mp4EsdsParser.ParseAudioSpecificConfig(esdsBytes);
        }
    }

    public class MovStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
