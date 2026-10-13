namespace EggEncoder.Codecs.Wav
{
    // Encodes 16-bit PCM into IMA ADPCM (WAVE_FORMAT_IMA_ADPCM, tag 17) block data -- the encode
    // counterpart to ImaAdpcmDecoder's DecodeBlock, producing bytes that decoder can read back. Mirrors
    // DecodeBlock's own block layout exactly, in reverse:
    //
    // - Per channel, a 4-byte header: the block's own first raw sample verbatim (int16 LE) as that
    //   channel's starting predictor, a step-index byte, and a reserved byte (always 0 here, matching
    //   FFmpeg's own real encoder, which never writes anything else into it either). Each block resets
    //   its predictor to that block's own raw sample -- never carried over from the previous block --
    //   while step index DOES carry over continuously across blocks via the caller-owned
    //   ImaAdpcmDecoder.ChannelState array, matching WAVE_FORMAT_IMA_ADPCM's own block-independent
    //   (predictor-wise) but step-size-adaptive-across-blocks design, confirmed from FFmpeg's real
    //   adpcmenc.c (status->prev_sample is reset from the block's own first sample every block; the
    //   shared per-channel status struct, and therefore step_index, is never reset between blocks).
    // - After every channel's header, the remaining (samplesPerBlock - 1) samples per channel are
    //   encoded in 8-sample/4-byte groups, one channel's full group then the next channel's -- the
    //   exact same interleaving DecodeBlock's own data loop consumes.
    //
    // Each sample's nibble is chosen by ImaAdpcmDecoder.QuantizeNibble, which also commits it to the
    // shared ChannelState -- see that method's own doc comment for why this encoder deliberately does
    // NOT reuse FFmpeg's own ADPCM_IMA_WAV nibble-picking heuristic (it optimizes for a different,
    // FFmpeg-internal reconstruction formula than the one real decoders -- including this project's
    // own -- actually use to expand nibbles back).
    internal static class ImaAdpcmEncoder
    {
        // Encodes exactly one block (samplesPerBlock frames, channels interleaved) from
        // interleavedSamples into blockBytes, sized to the caller's own block-align byte count.
        // channelStates carries each channel's step index forward from the previous call; this method
        // always overwrites each channel's Predictor with that block's own first raw sample, per the
        // block-independent-predictor design above.
        internal static void EncodeBlock(ReadOnlySpan<int> interleavedSamples, int channels, int samplesPerBlock, ImaAdpcmDecoder.ChannelState[] channelStates, Span<byte> blockBytes)
        {
            var offset = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                // Clamped, not just truncated, for the same reason QuantizeNibble clamps its own
                // input -- an out-of-contract sample here would otherwise wrap silently (e.g.
                // int.MinValue truncates to 0 via a raw (short) cast) rather than saturate like every
                // other encode path in this project does for an out-of-range value.
                var firstSample = (short)Math.Clamp(interleavedSamples[channel], short.MinValue, short.MaxValue);
                channelStates[channel].Predictor = firstSample;

                blockBytes[offset] = (byte)firstSample;
                blockBytes[offset + 1] = (byte)(firstSample >> 8);
                blockBytes[offset + 2] = (byte)channelStates[channel].StepIndex;
                blockBytes[offset + 3] = 0; // reserved -- FFmpeg's own real encoder never writes anything else here either
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
                        var sampleIndex1 = 1 + groupStart + m;
                        var nibble1 = ImaAdpcmDecoder.QuantizeNibble(ref state, interleavedSamples[(sampleIndex1 * channels) + channel]);

                        var b = (byte)nibble1;
                        if (groupStart + m + 1 < remainingSamplesPerChannel)
                        {
                            var sampleIndex2 = sampleIndex1 + 1;
                            var nibble2 = ImaAdpcmDecoder.QuantizeNibble(ref state, interleavedSamples[(sampleIndex2 * channels) + channel]);
                            b |= (byte)(nibble2 << 4);
                        }

                        blockBytes[offset++] = b;
                    }
                }
            }
        }
    }
}
