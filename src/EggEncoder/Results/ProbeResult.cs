namespace EggEncoder.Results
{
    public class ProbeResult
    {
        public required string FormatName { get; init; }

        public required string FormatLongName { get; init; }

        public required long SizeBytes { get; init; }

        public required double DurationSeconds { get; init; }

        public required string CodecType { get; init; }

        public string? CodecName { get; init; }

        public string? CodecLongName { get; init; }

        public int? SampleRate { get; init; }

        public int? Channels { get; init; }

        public string? ChannelLayout { get; init; }

        public int? BitsPerSample { get; init; }

        public int? BitRate { get; init; }

        public bool? IsVariableBitRate { get; init; }

        public long? DurationInSamples { get; init; }

        public string? TimeBase { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public IReadOnlyList<double>? Waveform { get; init; }

        /// <summary>
        /// The single loudest absolute sample value across the whole decoded audio, normalized to
        /// 0.0..1.0 (1.0 = the bit depth's own full-scale maximum). Null for a file this probe
        /// couldn't decode audio from (e.g. MOV/MP4 with no matching audio track).
        /// </summary>
        public double? PeakAmplitude { get; init; }

        /// <summary>
        /// Root-mean-square level across every individual sample of the whole decoded audio,
        /// normalized the same way <see cref="PeakAmplitude"/> is. A rough loudness indicator --
        /// not a perceptual/ITU-R BS.1770 loudness measurement. Null for a file this probe
        /// couldn't decode audio from.
        /// </summary>
        public double? RmsLevel { get; init; }

        /// <summary>
        /// Integrated loudness in LUFS, per ITU-R BS.1770-4 / EBU R128 -- the real perceptual
        /// loudness measurement <see cref="RmsLevel"/>'s own doc comment says it is not. Null for
        /// a file this probe couldn't decode audio from, or for audio shorter than the standard's
        /// own minimum 400ms gating block. Can legitimately be <see cref="double.NegativeInfinity"/>
        /// for audio that's entirely at or below the standard's -70 LUFS absolute silence gate --
        /// that's a real measurement, not a sentinel for "couldn't measure" (which is what the
        /// null case means instead).
        /// </summary>
        public double? IntegratedLoudness { get; init; }

        /// <summary>
        /// This MP3 file's own de-facto "LAME tag" encoder delay, in samples -- how much of the
        /// file's total frame count * 1152 samples/frame is leading lookahead/warm-up silence
        /// rather than real audio, under the standard convention every LAME-tag-aware tool
        /// (including this one) uses for that total: every frame, including the dedicated Xing/
        /// Info header frame itself, contributes exactly 1152 samples. Null for every non-MP3
        /// format, and for an MP3 with no Xing/Info header or one with no LAME-style extension (a
        /// real, valid case on its own). <see cref="DurationSeconds"/> already accounts for this
        /// value when present, for a more accurate MP3 duration.
        ///
        /// Applying this value directly to a raw decode's own sample count needs care: some real
        /// decoders (including this project's own <c>Mp3Decoder.Decode</c>, via NLayer) recognize
        /// and silently exclude the Xing/Info header frame from their own decoded output entirely,
        /// rather than decoding it to 1152 samples of meaningless audio the way the standard
        /// convention above assumes -- for such a decoder's own raw output, skipping
        /// <see cref="EncoderDelaySamples"/> worth of samples from the front skips 1152 samples too
        /// many. This field reports the tag's own standard value, not a decoder-specific adjustment
        /// of it, the same way <c>Mp3ProbeResult</c>'s own fields never assume a specific decoder's
        /// quirks.
        /// </summary>
        public int? EncoderDelaySamples { get; init; }

        /// <summary>
        /// This MP3 file's own de-facto "LAME tag" encoder padding, in samples -- how much trailing
        /// flush silence a gapless-aware player should discard from the end of a raw decode, under
        /// the same "every frame, including the header frame, contributes 1152 samples" convention
        /// <see cref="EncoderDelaySamples"/>'s own doc comment explains (including its caveat about
        /// decoders, such as this project's own <c>Mp3Decoder.Decode</c>, that exclude the header
        /// frame from their own raw output). Null under the same conditions
        /// <see cref="EncoderDelaySamples"/> is.
        /// </summary>
        public int? EncoderPaddingSamples { get; init; }

        /// <summary>
        /// Metadata tags, keyed by their raw format-native id exactly as the container stores them
        /// (e.g. "INAM" for WAV's own title chunk, "TIT2" for MP3's own ID3v2 title frame) -- not
        /// remapped to friendlier names, the same way <see cref="CodecName"/> exposes "pcm_s16le"
        /// rather than a human label. Populated for WAV files with a 'LIST'/'INFO' chunk, and for
        /// MP3 files with an ID3v2.3/2.4 tag (text information frames only -- see
        /// <c>Codecs.Mp3.Id3v2Reader</c>'s own doc comment for the exact scope); null for every
        /// other format, and for a WAV/MP3 file that simply doesn't carry one.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Tags { get; init; }
    }
}
