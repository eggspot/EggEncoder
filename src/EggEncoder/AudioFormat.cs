namespace EggEncoder
{
    /// <summary>
    /// Identifies a supported audio (or audio/video container) format for the <see cref="Stream"/>-based
    /// <see cref="IMediaEncoder"/> overloads, which -- unlike the path-based ones -- have no file
    /// extension to dispatch on and so need the format named explicitly. Mirrors the extension-dispatch
    /// switch <see cref="Codecs.AudioCutter"/> already uses for the path-based API 1:1.
    /// </summary>
    public enum AudioFormat
    {
        /// <summary>WAV / WAVE (.wav).</summary>
        Wav,

        /// <summary>AIFF / AIFF-C (.aiff/.aif/.aifc).</summary>
        Aiff,

        /// <summary>Sun/NeXT AU (.au).</summary>
        Au,

        /// <summary>FLAC (.flac).</summary>
        Flac,

        /// <summary>MP3 / MPEG-1 Layer III (.mp3).</summary>
        Mp3,

        /// <summary>ADTS AAC (.aac).</summary>
        Aac,

        /// <summary>Windows Media Audio, in an ASF container (.wma).</summary>
        Wma,

        /// <summary>Apple Lossless (ALAC), in a CAF container (.caf).</summary>
        Caf,

        /// <summary>TTA / True Audio (.tta).</summary>
        Tta,

        /// <summary>Opus, in an OggOpus container (.opus).</summary>
        Opus,

        /// <summary>Ogg Vorbis (.ogg).</summary>
        Vorbis,

        /// <summary>WavPack (.wv).</summary>
        WavPack,

        /// <summary>QuickTime / MOV or MP4 (.mov/.mp4) -- metadata and, when present, a decodable mono AAC-LC audio track.</summary>
        Mov
    }
}
