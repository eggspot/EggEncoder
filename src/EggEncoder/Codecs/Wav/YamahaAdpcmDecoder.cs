namespace EggEncoder.Codecs.Wav
{
    // Decodes Yamaha ADPCM (WAVE_FORMAT_YAMAHA_ADPCM, format tag 0x0020/32) nibbles into 16-bit PCM.
    //
    // Unlike IMA ADPCM and MS ADPCM, this format has NO block structure at all -- confirmed directly
    // from FFmpeg's real decoder source (libavcodec/adpcm.c's CASE(ADPCM_YAMAHA, ...)): there is no
    // per-block header, no wSamplesPerBlock 'fmt' chunk extension (a real ffmpeg-produced fixture's
    // own 'fmt' chunk carries cbSize=0, no extension at all), and each channel's predictor/step state
    // carries over continuously for the entire stream. The "if (!c->step) { predictor = 0; step =
    // 127; }" lazy-init in ExpandNibble below only ever fires once per channel, on that channel's very
    // first nibble (c# default struct state already has Step == 0), not per block.
    //
    // Nibble order (confirmed from the same CASE block): each byte holds two nibbles, low nibble
    // first. For mono, one byte decodes TWO consecutive time frames (low nibble -> frame N, high
    // nibble -> frame N+1), both through the single channel's own state. For stereo, one byte decodes
    // ONE frame's two channels (low nibble -> channel 0, high nibble -> channel 1). Both cases are the
    // same general rule: nibbles are consumed in the exact order of the flat interleaved sample array
    // (frame-major, channel-minor) -- i.e. nibble index i belongs to channel (i % channels), frame
    // (i / channels) -- so WavReader drives this directly rather than needing a DecodeBlock-style
    // encapsulation IMA/MS ADPCM's own real block structure requires.
    internal static class YamahaAdpcmDecoder
    {
        // ff_adpcm_yamaha_difflookup, confirmed verbatim from FFmpeg's real libavcodec/adpcm_data.c.
        private static readonly sbyte[] DiffLookup =
        [
            1, 3, 5, 7, 9, 11, 13, 15,
            -1, -3, -5, -7, -9, -11, -13, -15
        ];

        // ff_adpcm_yamaha_indexscale, confirmed verbatim from FFmpeg's real libavcodec/adpcm_data.c --
        // symmetric (the first 8 entries repeat for the last 8) since the step-size scale doesn't
        // depend on the difference's sign, only its magnitude.
        private static readonly short[] IndexScale =
        [
            230, 230, 230, 230, 307, 409, 512, 614,
            230, 230, 230, 230, 307, 409, 512, 614
        ];

        internal struct ChannelState
        {
            public int Predictor;
            public int Step;
        }

        // Ported directly from FFmpeg's real adpcm_yamaha_expand_nibble. nibble is assumed already
        // masked to 0..15 by the caller (WavReader only ever passes a nibble extracted via `& 0x0F` or
        // `>> 4` from a byte), the same trust-the-caller convention ImaAdpcmDecoder/MsAdpcmDecoder's
        // own ExpandNibble already use for their own table lookups.
        internal static int ExpandNibble(ref ChannelState state, int nibble)
        {
            if (state.Step == 0)
            {
                state.Predictor = 0;
                state.Step = 127;
            }

            state.Predictor += (state.Step * DiffLookup[nibble]) / 8;
            state.Predictor = Math.Clamp(state.Predictor, short.MinValue, short.MaxValue);

            state.Step = (state.Step * IndexScale[nibble]) >> 8;
            state.Step = Math.Clamp(state.Step, 127, 24576);

            return state.Predictor;
        }
    }
}
