using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mov
{
    // Demuxes the video track of a MOV/MP4 file into raw per-sample access units -- byte offset,
    // byte size, and keyframe (sync-sample) flag -- without decoding any codec payload. This is the
    // video-track counterpart to MovDecoder's existing audio-sample demuxing, and foundational
    // infrastructure for a future video codec decoder (e.g. H.264/VP9/AV1): see
    // docs/video-support-backlog.md item 1. Not a public IMediaEncoder entry point on its own --
    // there is no decodable payload to expose yet, only the container-level access units.
    //
    // Only the first video track and its first sample description entry are read; multiple video
    // tracks, multiple sample description entries, and fragmented MP4 ('moof'/'mvex') are out of
    // scope here, matching MovDecoder's equivalent limitations on the audio side.
    internal static class MovVideoDemuxer
    {
        public static MovVideoTrackInfo DemuxVideoTrack(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            var moov = MovAtomReader.FindAtom(stream, "moov", 0, stream.Length)
                ?? throw new InvalidDataException($"'{filePath}' is not a valid MOV/MP4 file: missing 'moov' atom");

            var videoTrak = MovAtomReader.FindTrackByHandlerType(stream, moov, "vide")
                ?? throw new InvalidDataException($"'{filePath}' does not contain a video track");

            var mdia = MovAtomReader.FindAtom(stream, "mdia", videoTrak.ContentStart, videoTrak.ContentEnd)!.Value;
            var minf = MovAtomReader.FindAtom(stream, "minf", mdia.ContentStart, mdia.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': video track is missing an 'minf' atom");
            var stbl = MovAtomReader.FindAtom(stream, "stbl", minf.ContentStart, minf.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': video track is missing an 'stbl' atom");
            var stsd = MovAtomReader.FindAtom(stream, "stsd", stbl.ContentStart, stbl.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}': video track is missing an 'stsd' atom");

            var codecFourCc = ReadFirstSampleEntryFourCc(stream, stsd, filePath);

            var samples = Mp4SampleTable.ReadSamples(stream, stbl);
            var syncSamples = Mp4SampleTable.ReadSyncSamples(stream, stbl);

            var videoSamples = new List<MovVideoSample>(samples.Count);
            for (var i = 0; i < samples.Count; i++)
            {
                var isKeyframe = syncSamples is null || syncSamples.Contains(i);
                videoSamples.Add(new MovVideoSample
                {
                    Offset = samples[i].Offset,
                    Size = samples[i].Size,
                    IsKeyframe = isKeyframe
                });
            }

            return new MovVideoTrackInfo
            {
                CodecFourCc = codecFourCc,
                Samples = videoSamples
            };
        }

        private static string ReadFirstSampleEntryFourCc(Stream stream, MovAtom stsd, string filePath)
        {
            stream.Position = stsd.ContentStart + 4; // version+flags
            Span<byte> entryCountBuffer = stackalloc byte[4];
            stream.ReadExactly(entryCountBuffer);
            var entryCount = BinaryPrimitives.ReadUInt32BigEndian(entryCountBuffer);

            if (entryCount == 0)
            {
                throw new InvalidDataException($"'{filePath}': 'stsd' has no sample description entries");
            }

            Span<byte> sampleEntryHeader = stackalloc byte[8];
            stream.ReadExactly(sampleEntryHeader);
            return Encoding.ASCII.GetString(sampleEntryHeader[4..8]);
        }
    }

    internal sealed class MovVideoSample
    {
        public required long Offset { get; init; }

        public required int Size { get; init; }

        public required bool IsKeyframe { get; init; }
    }

    internal sealed class MovVideoTrackInfo
    {
        public required string CodecFourCc { get; init; }

        public required IReadOnlyList<MovVideoSample> Samples { get; init; }
    }
}
