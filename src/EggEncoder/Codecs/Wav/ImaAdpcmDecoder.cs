namespace EggEncoder.Codecs.Wav
{
    // Decodes IMA ADPCM (WAVE_FORMAT_IMA_ADPCM / WAVE_FORMAT_DVI_ADPCM, tag 17) block data into 16-bit
    // PCM, and (via QuantizeNibble below) backs ImaAdpcmEncoder's own encode direction with the exact
    // same nibble<->diff mapping, so encode and decode can never silently drift out of sync with each
    // other. This is a from-scratch, pure managed implementation of a long-standing (early 1990s),
    // patent-expired, extensively documented algorithm -- no third-party dependency needed at all,
    // unlike WavPack (which had no viable pure-managed option).
    //
    // Every constant and formula here was cross-verified against FFmpeg's own real decoder source
    // (libavcodec/adpcm.c's ff_adpcm_ima_qt_expand_nibble -- see ExpandNibble's own comment below
    // for why it's that function, not the differently-shaped adpcm_ima_expand_nibble one might
    // expect) and a real ffmpeg-produced reference file's actual bytes, not just a written
    // specification -- the same "verify against a real, authoritative implementation" discipline
    // this project has used for every codec since WavPack's own API turned out to need two rounds
    // of real-CI correction. QuantizeNibble's own doc comment below has a second example of this same
    // discipline catching a real, numeric discrepancy in FFmpeg's own source before any encoder code
    // was written against it.
    internal static class ImaAdpcmDecoder
    {
        // The 89-entry step size table and 16-entry step-index adjustment table are the fixed,
        // standardized tables every real IMA ADPCM implementation shares (FFmpeg, Microsoft ACM,
        // QuickTime, etc.) -- confirmed against FFmpeg's own ff_adpcm_step_table/ff_adpcm_index_table.
        private static readonly int[] _stepTable =
        [
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
            34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
            3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        ];

        private static readonly int[] _indexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

        internal struct ChannelState
        {
            public int Predictor;
            public int StepIndex;
        }

        // Matches FFmpeg's ff_adpcm_ima_qt_expand_nibble exactly -- the function real FFmpeg (verified
        // directly from libavcodec/adpcm.c, not a summary) actually calls for standard 4-bit WAV IMA
        // ADPCM, confirmed bit-exact against both FFmpeg's and Apple's own CoreAudio (afconvert)
        // independent decode of the same real file.
        //
        // This is deliberately NOT ((2*delta+1)*step)>>3 (the formula FFmpeg's OTHER, generic
        // adpcm_ima_expand_nibble uses for other variants) -- an earlier version of this code assumed
        // the two were algebraically interchangeable, verified "on paper" by distributing the shift
        // across each bit term. That distribution is only valid when step is a multiple of 8: each term
        // below is right-shifted (truncated) SEPARATELY before summing, which is a genuinely different
        // (and lossier) result than right-shifting the full product once at the end whenever step isn't
        // a multiple of 8. E.g. step=7, delta=7: this formula gives 0+7+3+1=11; the single-shift formula
        // gives (15*7)>>3=13. Caught by cross-checking real decode output against two independent
        // real-world decoders rather than trusting a hand-derived "proof" of equivalence.
        internal static int ExpandNibble(ref ChannelState state, int nibble)
        {
            var step = _stepTable[state.StepIndex];

            var stepIndex = state.StepIndex + _indexTable[nibble];
            state.StepIndex = Math.Clamp(stepIndex, 0, _stepTable.Length - 1);

            var diff = ComputeDiff(step, nibble);

            var predictor = state.Predictor + ((nibble & 8) != 0 ? -diff : diff);
            state.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);

            return state.Predictor;
        }

        // The per-bit-term magnitude sum shared by ExpandNibble (decode) and QuantizeNibble (encode)'s
        // own search below -- extracted so both sides are guaranteed to agree on what a given nibble
        // actually means, rather than the encoder re-deriving the same formula a second time and
        // risking it drifting out of sync with this one.
        private static int ComputeDiff(int step, int nibble)
        {
            var diff = step >> 3;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 1) != 0) diff += step >> 2;
            return diff;
        }

        // Picks the nibble (0-15) whose ComputeDiff-decoded magnitude is CLOSEST to the sample's actual
        // distance from the channel's current predictor, then commits it via ExpandNibble itself --
        // guaranteeing the encoder's running predictor/step-index track in perfect lockstep with any
        // standards-compliant IMA ADPCM decoder (this project's own ExpandNibble-based WavReader decode
        // included), by construction, rather than by re-deriving the update formula a second time in a
        // separate encoder and hoping it happens to match.
        //
        // Deliberately NOT FFmpeg's own adpcm_ima_compress_sample closed-form guess
        // (nibble = min(7, abs(delta)*4/step)): verified directly from FFmpeg's real adpcmenc.c source
        // that function updates its OWN internal state via a DIFFERENT, single-multiply
        // ff_adpcm_yamaha_difflookup-table formula than the one FFmpeg's real ADPCM_IMA_WAV decoder
        // actually uses to expand nibbles back (ff_adpcm_ima_qt_expand_nibble, i.e. this file's own
        // ComputeDiff) -- confirmed numerically to disagree (step=7, nibble magnitude 1: ComputeDiff
        // gives diff=1, the Yamaha-table formula gives diff=2), so reusing FFmpeg's own nibble-picking
        // heuristic here would have picked nibbles optimized for a reconstruction formula this project
        // doesn't use to decode them. An 8-candidate brute-force search against this file's own real
        // ComputeDiff is exact and just as cheap.
        internal static int QuantizeNibble(ref ChannelState state, int sample)
        {
            // Clamped defensively to this format's own documented 16-bit input contract, the same way
            // G711Codec.ClampToTableIndex guards its own encode entry point against an out-of-contract
            // caller -- without this, a sample of exactly int.MinValue makes delta also int.MinValue
            // (state.Predictor is always already within short range), and Math.Abs(int.MinValue) throws
            // OverflowException (confirmed by actually triggering it, not assumed) rather than this
            // method degrading gracefully like every other encode path in this file does.
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);

            var delta = sample - state.Predictor;
            var sign = delta < 0 ? 8 : 0;
            var magnitude = Math.Abs(delta);
            var step = _stepTable[state.StepIndex];

            var bestMagnitudeNibble = 0;
            var bestError = int.MaxValue;
            for (var m = 0; m <= 7; m++)
            {
                var error = Math.Abs(magnitude - ComputeDiff(step, m));
                if (error < bestError)
                {
                    bestError = error;
                    bestMagnitudeNibble = m;
                }
            }

            var nibble = sign | bestMagnitudeNibble;
            ExpandNibble(ref state, nibble);

            return nibble;
        }

        // Decodes exactly one block's worth of samples into interleavedOutput (sized to
        // samplesPerBlock * channels). Per channel, the block starts with a 4-byte header (int16 LE
        // predictor, a step-index byte, a reserved byte) whose predictor becomes that channel's first
        // sample verbatim (not nibble-decoded) -- then the remaining bytes are consumed in interleaved
        // 4-byte/8-sample groups per channel (channel 0's next 8 samples, then channel 1's next 8,
        // repeating), not fully separated per channel and not simple byte-for-byte interleaving.
        // Mirrors FFmpeg's own decode loop exactly (verified directly from its source).
        internal static void DecodeBlock(ReadOnlySpan<byte> blockBytes, int channels, int samplesPerBlock, ChannelState[] channelStates, int[] interleavedOutput)
        {
            var offset = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var predictor = (short)(blockBytes[offset] | (blockBytes[offset + 1] << 8));
                var stepIndex = blockBytes[offset + 2];

                channelStates[channel] = new ChannelState
                {
                    Predictor = predictor,
                    StepIndex = Math.Clamp(stepIndex, 0, _stepTable.Length - 1)
                };

                interleavedOutput[channel] = predictor;
                offset += 4;
            }

            var remainingSamplesPerChannel = samplesPerBlock - 1;
            for (var groupStart = 0; groupStart < remainingSamplesPerChannel; groupStart += 8)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    ref var state = ref channelStates[channel];

                    for (var m = 0; m < 8 && groupStart + m < remainingSamplesPerChannel; m += 2)
                    {
                        var b = blockBytes[offset++];

                        var sampleIndex1 = 1 + groupStart + m;
                        interleavedOutput[(sampleIndex1 * channels) + channel] = ExpandNibble(ref state, b & 0x0F);

                        if (groupStart + m + 1 < remainingSamplesPerChannel)
                        {
                            var sampleIndex2 = sampleIndex1 + 1;
                            interleavedOutput[(sampleIndex2 * channels) + channel] = ExpandNibble(ref state, b >> 4);
                        }
                    }
                }
            }
        }
    }
}
