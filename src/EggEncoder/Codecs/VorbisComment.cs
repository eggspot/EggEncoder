using System.Text;

namespace EggEncoder.Codecs
{
    // The Vorbis comment metadata convention (vendor string + a list of "FIELD=value" entries,
    // all 32-bit length fields little-endian -- unlike FLAC's own big-endian bitstream, this is a
    // Vorbis-spec convention that predates and is shared across every container that embeds it),
    // shared by every format that uses it: embedded directly as FLAC's own VORBIS_COMMENT metadata
    // block (see FlacDecoder/FlacEncoder), or inside an Ogg "OpusTags"/Vorbis comment header packet
    // for Opus/Vorbis (a thin, container-specific wrapper this type doesn't know about -- it only
    // reads/writes the vendor+comment-list bytes themselves, not any outer framing).
    //
    // Read is bounds-checked against the caller-supplied byte range and degrades gracefully (never
    // throws) on anything malformed -- this metadata is decorative, the same "stop at the first
    // thing we can't handle, return whatever we got" convention WavReader.Tags/Id3v2Reader already
    // established. Keyed by the raw field name exactly as stored (not normalized to uppercase,
    // even though real-world taggers conventionally write upper-case names like "TITLE"/"ARTIST"),
    // not remapped to friendlier names.
    //
    // Write is strict: a field name must be non-empty, printable ASCII (0x20-0x7E), and must not
    // contain '=' (the field/value separator) -- the same strictness precedent as
    // Id3v2Writer/WavWriter.Create's own tag validation -- or BuildCommentListBytes throws
    // ArgumentException immediately.
    internal static class VorbisCommentReader
    {
        public static IReadOnlyDictionary<string, string> ReadFrom(byte[] buffer, int offset, int length)
        {
            var tags = new Dictionary<string, string>();

            // Clamped to the buffer's own actual length, not just the caller-declared range -- a
            // truncated file can declare a block length that reaches past the real end of the
            // buffer, and every bounds check below only guards against `end`, so without this
            // clamp a truncated file would throw IndexOutOfRangeException instead of degrading
            // gracefully like every other malformed-input case this type handles.
            var end = Math.Min(offset + length, buffer.Length);
            var position = offset;

            if (!TryReadUInt32LittleEndian(buffer, ref position, end, out var vendorLength) ||
                !TryAdvancePast(ref position, end, vendorLength))
            {
                return tags;
            }

            if (!TryReadUInt32LittleEndian(buffer, ref position, end, out var commentCount))
            {
                return tags;
            }

            for (var i = 0; i < commentCount; i++)
            {
                if (!TryReadUInt32LittleEndian(buffer, ref position, end, out var commentLength) ||
                    !TryReadUtf8String(buffer, ref position, end, commentLength, out var comment))
                {
                    break;
                }

                var separatorIndex = comment.IndexOf('=');
                if (separatorIndex > 0)
                {
                    tags[comment[..separatorIndex]] = comment[(separatorIndex + 1)..];
                }
            }

            return tags;
        }

        private static bool TryReadUInt32LittleEndian(byte[] buffer, ref int position, int end, out uint value)
        {
            if (position + 4 > end)
            {
                value = 0;
                return false;
            }

            value = (uint)(buffer[position] | (buffer[position + 1] << 8) | (buffer[position + 2] << 16) | (buffer[position + 3] << 24));
            position += 4;
            return true;
        }

        private static bool TryAdvancePast(ref int position, int end, uint length)
        {
            if ((long)position + length > end)
            {
                return false;
            }

            position += (int)length;
            return true;
        }

        private static bool TryReadUtf8String(byte[] buffer, ref int position, int end, uint length, out string value)
        {
            if ((long)position + length > end)
            {
                value = "";
                return false;
            }

            value = Encoding.UTF8.GetString(buffer, position, (int)length);
            position += (int)length;
            return true;
        }
    }

    internal static class VorbisCommentWriter
    {
        private const string VendorString = "EggEncoder";

        public static byte[] BuildCommentListBytes(IReadOnlyDictionary<string, string> tags)
        {
            using var stream = new MemoryStream();

            var vendorBytes = Encoding.UTF8.GetBytes(VendorString);
            WriteUInt32LittleEndian(stream, (uint)vendorBytes.Length);
            stream.Write(vendorBytes);

            WriteUInt32LittleEndian(stream, (uint)tags.Count);

            foreach (var (key, value) in tags)
            {
                if (!IsValidFieldName(key))
                {
                    throw new ArgumentException($"Invalid Vorbis comment field name '{key}' -- must be non-empty, printable ASCII, and must not contain '='", nameof(tags));
                }

                var commentBytes = Encoding.UTF8.GetBytes($"{key}={value}");
                WriteUInt32LittleEndian(stream, (uint)commentBytes.Length);
                stream.Write(commentBytes);
            }

            return stream.ToArray();
        }

        private static bool IsValidFieldName(string key)
        {
            if (key.Length == 0)
            {
                return false;
            }

            foreach (var c in key)
            {
                if (c is '=' or < (char)0x20 or > (char)0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        private static void WriteUInt32LittleEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
