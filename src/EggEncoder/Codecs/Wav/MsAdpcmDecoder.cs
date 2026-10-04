namespace EggEncoder.Codecs.Wav
{
    // Decodes MS ADPCM (WAVE_FORMAT_ADPCM, format tag 2) block data into 16-bit PCM. Unlike IMA ADPCM,
    // this format carries its own per-file coefficient table in the 'fmt ' chunk extension rather than
    // using one universal fixed table -- this decoder takes that table as a parameter rather than
    // hardcoding it, so it works correctly even against a (rare) file with a non-standard table, not
    // just the standard one every real encoder actually emits.
    //
    // Every formula and the block/header layout were verified directly against FFmpeg's real decoder
    // source (libavcodec/adpcm.c's adpcm_ms_expand_nibble and its ADPCM_MS block-decode case,
    // libavcodec/adpcm_data.c's ff_adpcm_AdaptationTable/AdaptCoeff1/AdaptCoeff2) and a real
    // ffmpeg-produced reference file's actual bytes -- including the block header's own field
    // grouping, which is NOT simply "repeat a per-channel header block twice" for stereo (see
    // DecodeBlock's own comment) -- then cross-checked bit-exact against two independent real-world
    // decoders (ffmpeg's own decode and macOS's afconvert/CoreAudio) before this file was written,
    // applying the lesson IMA ADPCM's own nibble-formula bug taught: verify against real output,
    // don't trust a read-through of the spec or a hand-derived formula alone.
    internal static class MsAdpcmDecoder
    {
        private static readonly int[] _adaptationTable =
        [
            230, 230, 230, 230, 307, 409, 512, 614,
            768, 614, 512, 409, 307, 230, 230, 230
        ];

        internal struct ChannelState
        {
            public int Coeff1;
            public int Coeff2;
            public int Delta;
            public int Sample1;
            public int Sample2;
        }

        // Ported directly from FFmpeg's adpcm_ms_expand_nibble. Divides by 256, not FFmpeg's own 64 --
        // FFmpeg's stored AdaptCoeff1/2 constants are pre-divided by 4 (documented in its own source
        // comment) purely so they fit in 8-bit integers; dividing a pre-divided coefficient by 64 is
        // mathematically identical to dividing the real, full-range coefficient by 256 (confirmed: every
        // real coefficient is exactly divisible by 4, so that pre-division never truncates). This
        // decoder reads the real, full-range coefficients directly from the file's own table instead of
        // pre-dividing them, so it divides by 256 to match.
        internal static int ExpandNibble(ref ChannelState state, int nibble)
        {
            var predictor = ((state.Sample1 * state.Coeff1) + (state.Sample2 * state.Coeff2)) / 256;
            predictor += ((nibble & 0x08) != 0 ? nibble - 0x10 : nibble) * state.Delta;
            predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);

            state.Sample2 = state.Sample1;
            state.Sample1 = predictor;

            var delta = (_adaptationTable[nibble] * state.Delta) >> 8;
            state.Delta = Math.Max(delta, 16);

            return state.Sample1;
        }

        // Decodes exactly one block's worth of samples into interleavedOutput (sized to
        // samplesPerBlock * channels). The block header is NOT "repeat a 7-byte per-channel header
        // block twice" for stereo -- its fields are grouped by field, not by channel: all channels'
        // block-predictor bytes first, then all channels' deltas, then all channels' sample1s, then all
        // channels' sample2s (verified directly from FFmpeg's real decode loop, which reads them in
        // exactly that grouped order). The header's own sample2/sample1 values become the block's first
        // two output frames verbatim (sample2 first, chronologically earlier; sample1 second) -- not
        // nibble-decoded -- then the remaining bytes are consumed: for stereo, each byte is one frame
        // (high nibble -> channel 0, low nibble -> channel 1); for mono, each byte is two consecutive
        // samples (both nibbles -> the one channel), a materially simpler interleaving than IMA ADPCM's
        // own 8-sample-group scheme.
        internal static void DecodeBlock(ReadOnlySpan<byte> blockBytes, int channels, int samplesPerBlock, ChannelState[] channelStates, short[] coeff1Table, short[] coeff2Table, int[] interleavedOutput)
        {
            var offset = 0;

            // The block predictor is a byte read from the compressed block data itself (not a
            // structural header field WavReader can validate once up front at Open() time) -- a
            // corrupted or adversarial block could contain any value 0..255, so it's clamped into the
            // real coefficient table's bounds here, every block, the same defensive pattern
            // ImaAdpcmDecoder already uses for its own per-nibble step-index table lookup.
            Span<byte> blockPredictor = stackalloc byte[channels];
            for (var channel = 0; channel < channels; channel++)
            {
                blockPredictor[channel] = (byte)Math.Clamp(blockBytes[offset++], 0, coeff1Table.Length - 1);
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Coeff1 = coeff1Table[blockPredictor[channel]];
                channelStates[channel].Coeff2 = coeff2Table[blockPredictor[channel]];
                channelStates[channel].Delta = (short)(blockBytes[offset] | (blockBytes[offset + 1] << 8));
                offset += 2;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Sample1 = (short)(blockBytes[offset] | (blockBytes[offset + 1] << 8));
                offset += 2;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Sample2 = (short)(blockBytes[offset] | (blockBytes[offset + 1] << 8));
                offset += 2;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                interleavedOutput[channel] = channelStates[channel].Sample2;
                interleavedOutput[channels + channel] = channelStates[channel].Sample1;
            }

            var remainingSamplesPerChannel = samplesPerBlock - 2;

            if (channels == 1)
            {
                ref var state = ref channelStates[0];
                var sampleIndex = 2;
                for (var i = 0; i < remainingSamplesPerChannel; i += 2)
                {
                    var b = blockBytes[offset++];
                    interleavedOutput[sampleIndex++] = ExpandNibble(ref state, b >> 4);

                    if (i + 1 < remainingSamplesPerChannel)
                    {
                        interleavedOutput[sampleIndex++] = ExpandNibble(ref state, b & 0x0F);
                    }
                }
            }
            else
            {
                var frameIndex = 2;
                for (var i = 0; i < remainingSamplesPerChannel; i++)
                {
                    var b = blockBytes[offset++];
                    var outputOffset = frameIndex * channels;

                    interleavedOutput[outputOffset] = ExpandNibble(ref channelStates[0], b >> 4);
                    interleavedOutput[outputOffset + 1] = ExpandNibble(ref channelStates[1], b & 0x0F);
                    frameIndex++;
                }
            }
        }
    }
}
