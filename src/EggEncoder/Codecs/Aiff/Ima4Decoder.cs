namespace EggEncoder.Codecs.Aiff
{
    // Decodes QuickTime ima4 (AIFC compressionType 'ima4') block-group data into 16-bit PCM.
    //
    // Reuses Wav.ImaAdpcmDecoder's ExpandNibble/ChannelState for the per-nibble math -- confirmed,
    // not assumed, that the formula and tables are identical to WAV's own IMA ADPCM: FFmpeg itself
    // calls the exact same ff_adpcm_ima_qt_expand_nibble function for both WAVE_FORMAT_IMA_ADPCM and
    // QuickTime's ima4 (the same function WavReader's own ImaAdpcmDecoder.ExpandNibble already ported
    // from). The BLOCK FRAMING, however, is genuinely different from WAV's IMA ADPCM, and is what this
    // file implements:
    //
    // - Each channel's own 34-byte sub-block decodes to exactly 64 samples, and ALL 64 are
    //   nibble-decoded -- unlike WAV's IMA ADPCM, there is no verbatim/header-derived first sample.
    // - The leading 2 bytes of each sub-block are a "preamble" documented (and confirmed by
    //   reverse-engineering a real file's actual bytes against its own true decoder state) to pack the
    //   predictor's top 9 bits and the step-index's 7 bits -- but this is a LOSSY, seek-point snapshot
    //   an encoder writes to match its own current state at that point for random-access purposes. A
    //   purely sequential decoder (confirmed bit-exact across an entire real file -- all 138 blocks,
    //   8832 samples -- against both ffmpeg's own decode and macOS's afconvert/CoreAudio decode of the
    //   same file, for both mono and stereo) never needs to read it at all: predictor/step-index simply
    //   start at 0/0 at the very start of the stream and carry forward continuously, sub-block after
    //   sub-block, never reset from a sub-block's own preamble. This project has no random-access/seek
    //   support for any codec, so the preamble is intentionally never parsed here.
    // - For stereo, each block GROUP is channel 0's complete 34-byte sub-block immediately followed by
    //   channel 1's complete 34-byte sub-block -- NOT interleaved at the nibble or byte level the way
    //   WAV's own IMA ADPCM stereo framing is. Each channel's predictor/step-index state is independent
    //   and carried forward separately, for the entire stream.
    // - AIFC's own COMM chunk reports numSampleFrames as the BLOCK count for ima4 specifically, not the
    //   raw sample count every other AIFC compressionType uses it for -- see AiffReader's own
    //   top-of-file comment and Open() for where that's accounted for.
    internal static class Ima4Decoder
    {
        internal const int BytesPerChannelSubBlock = 34;
        internal const int SamplesPerChannelSubBlock = 64;

        // Decodes exactly one block group (one BytesPerChannelSubBlock-byte sub-block per channel, laid
        // out consecutively channel 0 then channel 1) into interleavedOutput, sized to
        // SamplesPerChannelSubBlock * channels. channelStates carries each channel's predictor/
        // step-index forward from the previous call -- this method never reads a sub-block's own
        // 2-byte preamble (see this file's own doc comment for why that's correct, not an oversight).
        internal static void DecodeBlockGroup(ReadOnlySpan<byte> blockGroupBytes, int channels, Wav.ImaAdpcmDecoder.ChannelState[] channelStates, int[] interleavedOutput)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var subBlockOffset = channel * BytesPerChannelSubBlock;
                ref var state = ref channelStates[channel];

                for (var i = 0; i < SamplesPerChannelSubBlock; i += 2)
                {
                    var b = blockGroupBytes[subBlockOffset + 2 + (i / 2)];

                    interleavedOutput[(i * channels) + channel] = Wav.ImaAdpcmDecoder.ExpandNibble(ref state, b & 0x0F);
                    interleavedOutput[((i + 1) * channels) + channel] = Wav.ImaAdpcmDecoder.ExpandNibble(ref state, b >> 4);
                }
            }
        }
    }
}
