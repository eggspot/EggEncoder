namespace EggEncoder.Codecs.Wav
{
    // Encodes 16-bit PCM into MS ADPCM (WAVE_FORMAT_ADPCM, tag 2) block data -- the encode
    // counterpart to MsAdpcmDecoder's DecodeBlock, producing bytes that decoder can read back. Mirrors
    // DecodeBlock's own block layout exactly, in reverse:
    //
    // - The block predictor byte is always 0 for every channel, selecting Coeff1Table[0]/Coeff2Table[0]
    //   (the simplest "predict next sample equals the previous one" pair) for the WHOLE block --
    //   confirmed directly from FFmpeg's real adpcmenc.c source that its own production encoder does
    //   exactly this (`int predictor = 0;`), with no per-block search over the other 6 standard pairs
    //   at all. This is a deliberate, verified match to real-world behavior, not a shortcut: MS
    //   ADPCM's own adaptive delta already does the heavy lifting within a block, the same way real
    //   encoders ship it.
    // - Delta (FFmpeg's own idelta) carries over continuously across blocks via the caller-owned
    //   MsAdpcmDecoder.ChannelState array, clamped up to this format's own floor of 16 immediately
    //   before each block's header write if it ever started below that (matching
    //   adpcm_ms_compress_sample's own floor) -- confirmed from FFmpeg's source this is carry-over,
    //   not a per-block reset, the same design IMA ADPCM's own step index already uses.
    // - Unlike FFmpeg's own real encoder (no upper bound on idelta at all), this encoder ALSO clamps
    //   Delta to short.MaxValue before writing it into the header: Delta can realistically exceed
    //   int16 range after a run of repeated maximum-magnitude nibbles (the adaptation table's own
    //   largest multiplier, 768/256=3x, compounds to exceed 32767 within single-digit repeated steps
    //   -- confirmed by computing it, not assumed), and the header field is only 16 bits. Writing an
    //   unclamped value there would silently wrap when cast to a signed 16-bit field, and -- just as
    //   importantly -- any such wrap must be applied to the running ChannelState itself, not just the
    //   bytes written, or this encoder's own internal state would diverge from what any decoder (this
    //   project's own included) reconstructs from the header it actually reads. The same defensive
    //   "clamp at the encode boundary" precedent WavWriter's own IMA ADPCM encode path and
    //   G711Codec.ClampToTableIndex already established.
    // - The block's own first two raw samples are written verbatim (Sample2 = chronologically first,
    //   Sample1 = second -- matching DecodeBlock's own field order), then the remaining
    //   (samplesPerBlock - 2) samples per channel are nibble-encoded: for mono, two samples per byte
    //   (high nibble then low nibble, the low nibble of a final odd-remainder byte left as 0 and
    //   never read back by DecodeBlock either); for stereo, one frame per byte (channel 0's nibble in
    //   the high bits, channel 1's in the low bits) -- the exact same interleaving DecodeBlock's own
    //   data loop consumes.
    internal static class MsAdpcmEncoder
    {
        // The standard 7-pair coefficient table every real MS ADPCM encoder emits (confirmed from
        // FFmpeg's own adpcm_data.c ff_adpcm_AdaptCoeff1/AdaptCoeff2, which stores these pre-divided
        // by 4 to fit 8-bit integers -- these are the real, non-pre-divided values this project's own
        // MsAdpcmDecoder.ExpandNibble divides by 256 instead, per its own doc comment). Index 0 --
        // (256, 0) -- is the pair this encoder always selects; the full table is still written into
        // every file's own 'fmt ' chunk extension, since that's what every real encoder does and what
        // a correct, standards-compliant reader (including this project's own WavReader) expects to
        // find there regardless of which single pair a given block actually uses.
        internal static readonly short[] Coeff1Table = [256, 512, 0, 192, 240, 460, 392];
        internal static readonly short[] Coeff2Table = [0, -256, 0, 64, 0, -208, -232];

        // Encodes exactly one block (samplesPerBlock frames, channels interleaved) from
        // interleavedSamples into blockBytes, sized to the caller's own block-align byte count.
        // channelStates carries each channel's Delta forward from the previous call; this method
        // always overwrites each channel's Coeff1/Coeff2 (to this file's own fixed index-0 pair) and
        // Sample1/Sample2 (to that block's own first two raw samples), per the design above.
        internal static void EncodeBlock(ReadOnlySpan<int> interleavedSamples, int channels, int samplesPerBlock, MsAdpcmDecoder.ChannelState[] channelStates, Span<byte> blockBytes)
        {
            var offset = 0;

            for (var channel = 0; channel < channels; channel++)
            {
                blockBytes[offset++] = 0; // block predictor index -- always 0, see this file's own doc comment
                channelStates[channel].Coeff1 = Coeff1Table[0];
                channelStates[channel].Coeff2 = Coeff2Table[0];
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Delta = Math.Clamp(channelStates[channel].Delta, 16, short.MaxValue);
                var delta = (short)channelStates[channel].Delta;
                blockBytes[offset] = (byte)delta;
                blockBytes[offset + 1] = (byte)(delta >> 8);
                offset += 2;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Sample2 = (short)Math.Clamp(interleavedSamples[channel], short.MinValue, short.MaxValue);
            }

            for (var channel = 0; channel < channels; channel++)
            {
                channelStates[channel].Sample1 = (short)Math.Clamp(interleavedSamples[channels + channel], short.MinValue, short.MaxValue);
                var sample1 = (short)channelStates[channel].Sample1;
                blockBytes[offset] = (byte)sample1;
                blockBytes[offset + 1] = (byte)(sample1 >> 8);
                offset += 2;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                var sample2 = (short)channelStates[channel].Sample2;
                blockBytes[offset] = (byte)sample2;
                blockBytes[offset + 1] = (byte)(sample2 >> 8);
                offset += 2;
            }

            var remainingSamplesPerChannel = samplesPerBlock - 2;

            if (channels == 1)
            {
                ref var state = ref channelStates[0];
                var sampleIndex = 2;
                for (var i = 0; i < remainingSamplesPerChannel; i += 2)
                {
                    var nibbleHigh = MsAdpcmDecoder.CompressSample(ref state, interleavedSamples[sampleIndex++]);
                    var b = (byte)(nibbleHigh << 4);

                    if (i + 1 < remainingSamplesPerChannel)
                    {
                        var nibbleLow = MsAdpcmDecoder.CompressSample(ref state, interleavedSamples[sampleIndex++]);
                        b |= (byte)nibbleLow;
                    }

                    blockBytes[offset++] = b;
                }
            }
            else
            {
                var frameIndex = 2;
                for (var i = 0; i < remainingSamplesPerChannel; i++)
                {
                    var outputOffset = frameIndex * channels;
                    var nibbleHigh = MsAdpcmDecoder.CompressSample(ref channelStates[0], interleavedSamples[outputOffset]);
                    var nibbleLow = MsAdpcmDecoder.CompressSample(ref channelStates[1], interleavedSamples[outputOffset + 1]);
                    blockBytes[offset++] = (byte)((nibbleHigh << 4) | nibbleLow);
                    frameIndex++;
                }
            }
        }
    }
}
