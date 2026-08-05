using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Mov
{
    public static class MovProbe
    {
        public static MovProbeResult Probe(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            var moov = MovAtomReader.FindAtom(stream, "moov", 0, stream.Length)
                ?? throw new InvalidDataException($"'{filePath}' is not a valid MOV/MP4 file: missing 'moov' atom");

            var mvhd = MovAtomReader.FindAtom(stream, "mvhd", moov.ContentStart, moov.ContentEnd)
                ?? throw new InvalidDataException($"'{filePath}' is missing an 'mvhd' atom");

            var (timescale, duration) = ReadMvhd(stream, mvhd);

            int? width = null;
            int? height = null;
            string? codecFourCc = null;

            foreach (var trak in MovAtomReader.EnumerateAtoms(stream, moov.ContentStart, moov.ContentEnd))
            {
                if (trak.Type != "trak")
                {
                    continue;
                }

                var tkhd = MovAtomReader.FindAtom(stream, "tkhd", trak.ContentStart, trak.ContentEnd);
                if (tkhd is null)
                {
                    continue;
                }

                var (trackWidth, trackHeight) = ReadTkhd(stream, tkhd.Value);
                if (trackWidth <= 0 || trackHeight <= 0)
                {
                    continue;
                }

                width = trackWidth;
                height = trackHeight;
                codecFourCc = FindCodecFourCc(stream, trak);
                break;
            }

            var durationSeconds = timescale > 0 ? duration / (double)timescale : 0;

            return new MovProbeResult
            {
                DurationInSeconds = (int)durationSeconds,
                DurationSeconds = durationSeconds,
                Width = width,
                Height = height,
                CodecFourCc = codecFourCc
            };
        }

        private static (uint Timescale, ulong Duration) ReadMvhd(Stream stream, MovAtom mvhd)
        {
            stream.Position = mvhd.ContentStart;
            Span<byte> versionAndFlags = stackalloc byte[4];
            stream.ReadExactly(versionAndFlags);

            if (versionAndFlags[0] == 1)
            {
                stream.Position = mvhd.ContentStart + 20;
                Span<byte> buffer = stackalloc byte[12];
                stream.ReadExactly(buffer);
                return (BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]), BinaryPrimitives.ReadUInt64BigEndian(buffer[4..12]));
            }

            stream.Position = mvhd.ContentStart + 12;
            Span<byte> shortBuffer = stackalloc byte[8];
            stream.ReadExactly(shortBuffer);
            return (BinaryPrimitives.ReadUInt32BigEndian(shortBuffer[..4]), BinaryPrimitives.ReadUInt32BigEndian(shortBuffer[4..8]));
        }

        private static (int Width, int Height) ReadTkhd(Stream stream, MovAtom tkhd)
        {
            stream.Position = tkhd.ContentStart;
            Span<byte> versionByte = stackalloc byte[1];
            stream.ReadExactly(versionByte);

            var widthOffset = versionByte[0] == 1 ? 88 : 76;
            stream.Position = tkhd.ContentStart + widthOffset;

            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);

            var width = BinaryPrimitives.ReadInt32BigEndian(buffer[..4]) >> 16;
            var height = BinaryPrimitives.ReadInt32BigEndian(buffer[4..8]) >> 16;
            return (width, height);
        }

        private static string? FindCodecFourCc(Stream stream, MovAtom trak)
        {
            var mdia = MovAtomReader.FindAtom(stream, "mdia", trak.ContentStart, trak.ContentEnd);
            var minf = mdia is null ? null : MovAtomReader.FindAtom(stream, "minf", mdia.Value.ContentStart, mdia.Value.ContentEnd);
            var stbl = minf is null ? null : MovAtomReader.FindAtom(stream, "stbl", minf.Value.ContentStart, minf.Value.ContentEnd);
            var stsd = stbl is null ? null : MovAtomReader.FindAtom(stream, "stsd", stbl.Value.ContentStart, stbl.Value.ContentEnd);

            if (stsd is null)
            {
                return null;
            }

            stream.Position = stsd.Value.ContentStart + 12;
            Span<byte> fourCcBuffer = stackalloc byte[4];
            stream.ReadExactly(fourCcBuffer);
            return Encoding.ASCII.GetString(fourCcBuffer);
        }
    }

    public class MovProbeResult
    {
        public required int DurationInSeconds { get; init; }

        public required double DurationSeconds { get; init; }

        public required int? Width { get; init; }

        public required int? Height { get; init; }

        public required string? CodecFourCc { get; init; }
    }
}
