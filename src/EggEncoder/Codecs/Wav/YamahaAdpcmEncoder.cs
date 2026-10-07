namespace EggEncoder.Codecs.Wav
{
    // Encodes 16-bit PCM into Yamaha ADPCM nibbles -- the encode counterpart to YamahaAdpcmDecoder's
    // ExpandNibble.
    //
    // Confirmed directly from FFmpeg's real adpcmenc.c source (adpcm_yamaha_compress_sample): unlike
    // IMA ADPCM's own QuantizeNibble (an 8-candidate search, because FFmpeg's real IMA encoder's
    // nibble-picking heuristic diverges from its own decoder's state formula), Yamaha ADPCM's real
    // encoder computes the nibble via a DIRECT closed-form formula --
    // `FFMIN(7, abs(delta) * 4 / step) + (delta < 0) * 8` -- then commits it through the exact same
    // state-update formula ExpandNibble decodes with, the same "compute directly, then commit via the
    // shared, already-verified decode path" pattern MsAdpcmDecoder.CompressSample already established
    // for MS ADPCM (a different format, but the same structural guarantee: this encoder's own running
    // state tracks any standards-compliant decoder's in lockstep by construction, not by coincidence).
    internal static class YamahaAdpcmEncoder
    {
        internal static int CompressSample(ref YamahaAdpcmDecoder.ChannelState state, int sample)
        {
            // Clamped defensively to this format's own documented 16-bit input contract, the same
            // precedent ImaAdpcmDecoder.QuantizeNibble/MsAdpcmDecoder.CompressSample/
            // G711Codec.ClampToTableIndex already establish at their own encode entry points.
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);

            // Mirrors FFmpeg's own real adpcm_yamaha_compress_sample, which duplicates this exact
            // lazy-init check rather than relying on ExpandNibble's own copy of it below -- the nibble
            // computation itself (dividing by state.Step) needs step already seeded before it runs.
            if (state.Step == 0)
            {
                state.Predictor = 0;
                state.Step = 127;
            }

            var delta = sample - state.Predictor;
            var nibble = Math.Min(7, Math.Abs(delta) * 4 / state.Step) + (delta < 0 ? 8 : 0);

            YamahaAdpcmDecoder.ExpandNibble(ref state, nibble);

            return nibble;
        }
    }
}
