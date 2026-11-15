namespace EggEncoder.Media
{
    /// <summary>
    /// Recognises a media file by its first bytes, for inputs whose name carries no extension.
    /// </summary>
    public static class MediaFormatSniffer
    {
        /// <summary>How many leading bytes <see cref="DetectExtension(ReadOnlySpan{byte})"/> looks at.</summary>
        public const int HeaderLength = 64;

        /// <summary>
        /// The extension this library dispatches on for a file starting with <paramref name="header"/>
        /// (<c>.mp4</c>, <c>.wav</c>, <c>.aiff</c>, <c>.flac</c>, <c>.opus</c>, <c>.ogg</c>, <c>.mp3</c>,
        /// <c>.aac</c>, <c>.au</c>, <c>.caf</c>, <c>.tta</c>, <c>.wv</c>, <c>.wma</c>, <c>.flv</c> or
        /// <c>.jpg</c>), or null when it isn't recognised.
        /// </summary>
        public static string? DetectExtension(ReadOnlySpan<byte> header)
        {
            if (header.Length >= 8 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
            {
                return ".mp4";
            }

            if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                return ".wav";
            }

            if (header.Length >= 12 && header[..4].SequenceEqual("FORM"u8) && (header.Slice(8, 4).SequenceEqual("AIFF"u8) || header.Slice(8, 4).SequenceEqual("AIFC"u8)))
            {
                return ".aiff";
            }

            if (header.Length >= 4)
            {
                var magic = header[..4];
                if (magic.SequenceEqual("fLaC"u8))
                {
                    return ".flac";
                }

                if (magic.SequenceEqual("OggS"u8))
                {
                    return header.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";
                }

                if (magic.SequenceEqual(".snd"u8))
                {
                    return ".au";
                }

                if (magic.SequenceEqual("caff"u8))
                {
                    return ".caf";
                }

                if (magic.SequenceEqual("TTA1"u8))
                {
                    return ".tta";
                }

                if (magic.SequenceEqual("wvpk"u8))
                {
                    return ".wv";
                }

                if (magic.SequenceEqual((ReadOnlySpan<byte>)[0x30, 0x26, 0xB2, 0x75]))
                {
                    return ".wma";
                }
            }

            if (header.Length >= 3 && header[..3].SequenceEqual("FLV"u8))
            {
                return ".flv";
            }

            if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
            {
                return ".mp3";
            }

            if (header.Length >= 2 && header[0] == 0xFF)
            {
                if (header[1] == 0xD8)
                {
                    return ".jpg";
                }

                if ((header[1] & 0xF6) == 0xF0)
                {
                    return ".aac";
                }

                if ((header[1] & 0xE0) == 0xE0)
                {
                    return ".mp3";
                }
            }

            return null;
        }

        /// <summary>The extension for the file at <paramref name="path"/>, by its first bytes; null when not recognised.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static string? DetectExtension(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            Span<byte> header = stackalloc byte[HeaderLength];
            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return DetectExtension(header[..read]);
        }

        /// <summary>
        /// The extension of <paramref name="path"/> in lower case, or the sniffed one when the name has
        /// none; null when neither gives one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static string? ResolveExtension(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            var extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension) ? DetectExtension(path) : extension.ToLowerInvariant();
        }
    }
}
