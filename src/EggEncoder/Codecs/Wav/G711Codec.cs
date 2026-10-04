namespace EggEncoder.Codecs.Wav
{
    // Decodes/encodes G.711 companded PCM (ITU-T G.711, WAV format tags 6 (A-law) and 7 (mu-law)).
    // Both algorithms are purely formulaic with no adaptive state or block structure at all -- unlike
    // IMA ADPCM, encode is just as tractable as decode here, so this codec supports both directions.
    //
    // Every formula and the encode table-construction algorithm were ported directly from FFmpeg's
    // real source (libavcodec/pcm_tablegen.h's alaw2linear/ulaw2linear/build_xlaw_table -- itself
    // "from g711.c by SUN microsystems (unrestricted use)", per that file's own comment, so there's no
    // license concern) and verified bit-exact against real ffmpeg-produced fixtures for BOTH decode
    // and encode, plus a second, independent cross-check of decode against macOS's own afconvert/
    // CoreAudio -- before writing a single line of this file, applying the lesson IMA ADPCM's earlier
    // nibble-formula bug taught: a formula that looks right still needs real-world verification.
    internal static class G711Codec
    {
        private const int Bias = 0x84;

        private static readonly byte[] _alawEncodeTable = BuildEncodeTable(DecodeALaw, mask: 0xd5);
        private static readonly byte[] _mulawEncodeTable = BuildEncodeTable(DecodeMuLaw, mask: 0xff);

        // Ported directly from FFmpeg's alaw2linear.
        internal static int DecodeALaw(byte coded)
        {
            var a = coded ^ 0x55;
            var quant = a & 0xf;
            var segment = (a & 0x70) >> 4;
            var magnitude = segment != 0 ? (quant + quant + 1 + 32) << (segment + 2) : (quant + quant + 1) << 3;

            return (a & 0x80) != 0 ? magnitude : -magnitude;
        }

        // Ported directly from FFmpeg's ulaw2linear. Note mu-law has two distinct byte codes for zero
        // (0x7F and 0xFF -- "positive" and "negative" zero, a documented quirk of its sign-magnitude
        // representation at the zero crossing): both decode to 0 here, and EncodeMuLaw(0) always
        // returns the canonical 0xFF (matching FFmpeg's own encoder, confirmed against real
        // ffmpeg-encoded fixtures) -- so round-tripping 0x7F specifically through Decode-then-Encode
        // does not reproduce 0x7F bit-for-bit, even though every other one of the 256 byte values
        // does. This is real, verified G.711 behavior, not a transcription bug.
        internal static int DecodeMuLaw(byte coded)
        {
            var u = ~coded & 0xff;
            var magnitude = ((u & 0xf) << 3) + Bias;
            magnitude <<= (u & 0x70) >> 4;

            return (u & 0x80) != 0 ? Bias - magnitude : magnitude - Bias;
        }

        internal static byte EncodeALaw(int sample) => _alawEncodeTable[ClampToTableIndex(sample)];

        internal static byte EncodeMuLaw(int sample) => _mulawEncodeTable[ClampToTableIndex(sample)];

        // The real encode table is indexed 0..16383 ((sample + 32768) >> 2 for a genuine int16-range
        // sample); clamped defensively so a sample outside that range -- which, unlike every other
        // bytesPerSample case in WavWriter, would otherwise index straight past the array -- fails safe
        // by saturating instead of throwing IndexOutOfRangeException.
        private static int ClampToTableIndex(int sample) => Math.Clamp((sample + 32768) >> 2, 0, 16383);

        // Ported directly from FFmpeg's build_xlaw_table: builds the encode table by evaluating the
        // decode function at every one of its 128 non-negative codes and locating, for each, the
        // midpoint linear threshold where the next code's decoded value takes over -- the standard way
        // to construct an exact encoder from a decoder for a non-linear companding scheme like this,
        // rather than hand-deriving a separate closed-form encode formula.
        private static byte[] BuildEncodeTable(Func<byte, int> decode, int mask)
        {
            var table = new byte[16384];
            table[8192] = (byte)mask;

            var j = 1;
            for (var i = 0; i < 127; i++)
            {
                var v1 = decode((byte)(i ^ mask));
                var v2 = decode((byte)((i + 1) ^ mask));
                var threshold = (v1 + v2 + 4) >> 3;

                for (; j < threshold; j++)
                {
                    table[8192 - j] = (byte)(i ^ (mask ^ 0x80));
                    table[8192 + j] = (byte)(i ^ mask);
                }
            }

            for (; j < 8192; j++)
            {
                table[8192 - j] = (byte)(127 ^ (mask ^ 0x80));
                table[8192 + j] = (byte)(127 ^ mask);
            }

            table[0] = table[1];

            return table;
        }
    }
}
