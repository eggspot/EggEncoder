using System.Text;

namespace EggEncoder.Codecs.Mp3
{
    // ID3v2 metadata tag support (read AND write) -- the near-universal tagging convention for MP3,
    // layered on top of/around the MPEG frames rather than part of the bitstream itself (unlike
    // WAV's own 'LIST'/'INFO' convention, which is just another RIFF chunk alongside 'data').
    // Scoped to text information frames only (any frame ID starting with 'T', except TXXX's own
    // user-defined description+value structure, out of scope the same way AIFC's MAC3/WavPack's
    // hybrid mode are) -- the frame family that covers every common tag (TIT2 title, TPE1 artist,
    // TALB album, TYER/TDRC year/date, TRCK track, TCON genre, and so on), keyed by the raw
    // four-character frame ID exactly as ID3v2 stores it, not remapped to friendlier names -- the
    // same convention WavReader.Tags already established for WAV's own chunk IDs.
    //
    // Read supports both ID3v2.3 (plain big-endian frame sizes) and ID3v2.4 (syncsafe frame sizes);
    // any other major version, or a tag using the extended header or whole-tag unsynchronization
    // (both real but rare in tags written by modern taggers), degrades gracefully to an empty tag
    // rather than throwing -- this metadata is decorative and unrelated to decoding the audio
    // itself, the same "stop at the first thing we can't handle, return whatever we got" convention
    // WavReader's own tag parsing already established. A per-frame compression/encryption/grouping/
    // unsynchronization flag is the same kind of "real but rare, skip gracefully" case -- rather than
    // decoding each version's own incompatible bit layout for that flags byte (2.3 and 2.4 assign
    // compression/encryption/grouping to different bits), any non-zero frame-format-flags byte at
    // all is treated as "don't decode this one frame's content", which is simpler and no less
    // correct for the frames every real-world tagger actually emits by default (unflagged).
    //
    // Write always emits a fresh ID3v2.4 tag (UTF-8 text, the modern, widely-supported choice).
    // Unlike read, write is strict: a tag id must be exactly 4 ASCII characters identifying a text
    // information frame (starts with 'T', not "TXXX") or BuildTagBytes throws ArgumentException
    // immediately -- the same strictness precedent as WavWriter.Create's own tag validation, since
    // writing a well-formed tag is a hard requirement here.
    internal static class Id3v2Reader
    {
        private const byte UnsynchronizationFlag = 0x80;
        private const byte ExtendedHeaderFlag = 0x40;

        public static IReadOnlyDictionary<string, string>? ReadTags(Stream stream)
        {
            if (stream.Length < 10)
            {
                return null;
            }

            stream.Position = 0;
            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);

            if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
            {
                return null;
            }

            var majorVersion = header[3];
            var headerFlags = header[5];
            var tagSize = ReadSyncSafeInt(header[6], header[7], header[8], header[9]);
            var framesEnd = 10 + tagSize;

            var tags = new Dictionary<string, string>();

            if ((majorVersion != 3 && majorVersion != 4) || (headerFlags & (UnsynchronizationFlag | ExtendedHeaderFlag)) != 0)
            {
                return tags;
            }

            var position = 10L;
            var frameHeader = new byte[10];
            while (position + 10 <= framesEnd)
            {
                stream.Position = position;
                stream.ReadExactly(frameHeader);

                if (!IsValidFrameId(frameHeader[..4]))
                {
                    // Zero-byte padding, which ID3v2.3/2.4 both allow at the end of the declared tag
                    // size, reaches here before that declared size runs out as often as not -- stop
                    // rather than trying to interpret padding as a frame.
                    break;
                }

                var frameId = Encoding.ASCII.GetString(frameHeader[..4]);
                var frameSize = majorVersion == 4
                    ? ReadSyncSafeInt(frameHeader[4], frameHeader[5], frameHeader[6], frameHeader[7])
                    : ReadBigEndianInt(frameHeader[4], frameHeader[5], frameHeader[6], frameHeader[7]);
                var formatFlags = frameHeader[9];

                position += 10;
                if (frameSize < 0 || position + frameSize > framesEnd)
                {
                    break;
                }

                if (formatFlags == 0 && frameId[0] == 'T' && frameId != "TXXX")
                {
                    var frameData = new byte[frameSize];
                    stream.Position = position;
                    stream.ReadExactly(frameData);

                    var text = DecodeText(frameData);
                    if (text is not null)
                    {
                        tags[frameId] = text;
                    }
                }

                position += frameSize;
            }

            return tags;
        }

        private static bool IsValidFrameId(ReadOnlySpan<byte> idBytes)
        {
            foreach (var b in idBytes)
            {
                if (b is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')))
                {
                    return false;
                }
            }

            return true;
        }

        private static string? DecodeText(ReadOnlySpan<byte> frameData)
        {
            if (frameData.Length < 1)
            {
                return null;
            }

            var encodingByte = frameData[0];
            var textBytes = frameData[1..];

            string text;
            switch (encodingByte)
            {
                case 0:
                    text = Encoding.Latin1.GetString(textBytes);
                    break;
                case 1:
                    text = DecodeUtf16WithBom(textBytes);
                    break;
                case 2:
                    text = Encoding.BigEndianUnicode.GetString(textBytes);
                    break;
                case 3:
                    text = Encoding.UTF8.GetString(textBytes);
                    break;
                default:
                    return null;
            }

            return text.TrimEnd('\0');
        }

        private static string DecodeUtf16WithBom(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes[2..]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            }

            // Malformed (no BOM) -- assume little-endian, the common real-world default.
            return Encoding.Unicode.GetString(bytes);
        }

        private static int ReadSyncSafeInt(byte b0, byte b1, byte b2, byte b3) =>
            ((b0 & 0x7F) << 21) | ((b1 & 0x7F) << 14) | ((b2 & 0x7F) << 7) | (b3 & 0x7F);

        private static int ReadBigEndianInt(byte b0, byte b1, byte b2, byte b3) =>
            (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    }

    internal static class Id3v2Writer
    {
        public static byte[] BuildTagBytes(IReadOnlyDictionary<string, string> tags)
        {
            using var frameBytes = new MemoryStream();

            foreach (var (id, value) in tags)
            {
                var isValidId = id.Length == 4 && id[0] == 'T' && id != "TXXX" && IsAsciiFrameId(id);
                if (!isValidId)
                {
                    throw new ArgumentException($"Invalid ID3v2 tag id '{id}' -- must be exactly 4 ASCII characters identifying a text information frame (e.g. \"TIT2\"); \"TXXX\" (its own user-defined description+value structure) is not supported", nameof(tags));
                }

                var textBytes = Encoding.UTF8.GetBytes(value);

                frameBytes.Write(Encoding.ASCII.GetBytes(id));
                frameBytes.Write(WriteSyncSafeInt(1 + textBytes.Length));
                frameBytes.Write([0, 0]); // frame flags: none set
                frameBytes.WriteByte(3); // text encoding: UTF-8 (ID3v2.4)
                frameBytes.Write(textBytes);
            }

            var frames = frameBytes.ToArray();

            using var tag = new MemoryStream();
            tag.Write(Encoding.ASCII.GetBytes("ID3"));
            tag.WriteByte(4); // major version
            tag.WriteByte(0); // revision
            tag.WriteByte(0); // tag flags: none set
            tag.Write(WriteSyncSafeInt(frames.Length));
            tag.Write(frames);

            return tag.ToArray();
        }

        private static bool IsAsciiFrameId(string id)
        {
            foreach (var c in id)
            {
                if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] WriteSyncSafeInt(int value) =>
        [
            (byte)((value >> 21) & 0x7F),
            (byte)((value >> 14) & 0x7F),
            (byte)((value >> 7) & 0x7F),
            (byte)(value & 0x7F)
        ];
    }
}
