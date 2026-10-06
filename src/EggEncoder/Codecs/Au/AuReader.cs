using System.Buffers.Binary;
using System.Text;
using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.Au
{
    // Sun/NeXT AU (.au, magic ".snd", mime audio/basic) -- a much simpler fixed-header container than
    // WAV/AIFF: no chunk structure at all, just one 24-byte fixed header (magic, headerSize, dataSize,
    // encoding, sampleRate, channels), optionally followed by a variable-length annotation string (the
    // gap between byte 24 and the declared headerSize) that's never parsed here -- same "purely a
    // human-readable label, skip over it" treatment AIFC's own compressionName gets in AiffReader --
    // then raw big-endian samples with no byte-alignment/padding requirement at all (confirmed against a
    // real ffmpeg-produced fixture whose data region ends at an odd byte offset; unlike WAV's RIFF or
    // AIFF's FORM, AU has no IFF-style even-chunk-size convention to account for).
    //
    // Every encoding value and the header layout were verified against real ffmpeg-produced fixture
    // files' actual bytes (ffmpeg's own "au" muxer/demuxer), not just a read-through of the historical
    // Sun audio file format spec.
    //
    // encoding coverage, read AND write for all of them: 1 (8-bit ISDN mu-law, G.711 -- reusing
    // Wav.G711Codec rather than a second implementation), 2/3/4/5 (8/16/24/32-bit signed big-endian
    // integer PCM -- AU's 8-bit is signed, the same convention AIFF uses, not WAV's unsigned one), 6/7
    // (32/64-bit big-endian IEEE float, decoded at this codebase's usual int32-native-range scale -- both
    // report 32-bit PCM resolution, the same "this project's int[] model has no 64-bit representation"
    // reasoning AIFC's own fl64 already established), 27 (8-bit ISDN A-law, G.711). Every other defined
    // encoding (G.721/G.722/G.723 ADPCM variants, fragmented samples, 16-bit-with-emphasis -- values
    // 8-26) is real but obscure enough in practice, and algorithmically distant enough from everything
    // else in this project, that it's out of scope the same way AIFC's 'ima4' is -- Open() throws a
    // clear NotSupportedException naming the unrecognized encoding value rather than silently
    // misinterpreting it.
    public sealed class AuReader : IDisposable
    {
        private enum SampleEncoding
        {
            MuLaw = 1,
            Signed8 = 2,
            Signed16 = 3,
            Signed24 = 4,
            Signed32 = 5,
            Float32 = 6,
            Float64 = 7,
            ALaw = 27
        }

        // Sun AU's own "length unknown, read until EOF" sentinel -- a legitimate value a real streaming
        // encoder can write, unlike WAV/AIFF which have no equivalent. AuWriter never writes it (every
        // write here always knows its exact totalFrames up front, the same requirement WavWriter/
        // AiffWriter already have), but a real-world AU file using it must still be read correctly.
        private const uint UnknownDataSize = 0xFFFFFFFF;

        private readonly FileStream _stream;
        private readonly long _sampleDataLength;
        private readonly SampleEncoding _encoding;
        private readonly int _bytesPerDiskSample;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        private AuReader(FileStream stream, int channels, int sampleRate, int bitsPerSample, long totalSamples, long sampleDataStart, long sampleDataLength, SampleEncoding encoding, int bytesPerDiskSample)
        {
            _stream = stream;
            _sampleDataLength = sampleDataLength;
            _encoding = encoding;
            _bytesPerDiskSample = bytesPerDiskSample;

            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            TotalSamples = totalSamples;

            _stream.Seek(sampleDataStart, SeekOrigin.Begin);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int BitsPerSample { get; }

        public long TotalSamples { get; }

        public bool IsFloatFormat => IsFloat32 || IsFloat64;

        /// <summary>True for encoding 6 (32-bit float); false for everything else, including encoding 7 -- see <see cref="IsFloat64"/> to tell them apart (both report <see cref="BitsPerSample"/> as 32).</summary>
        public bool IsFloat32 => _encoding == SampleEncoding.Float32;

        /// <summary>True for encoding 7 (64-bit float); false for everything else, including encoding 6 -- see <see cref="IsFloat32"/> to tell them apart (both report <see cref="BitsPerSample"/> as 32, since this codebase's int[] PCM model has no 64-bit representation).</summary>
        public bool IsFloat64 => _encoding == SampleEncoding.Float64;

        public bool IsALaw => _encoding == SampleEncoding.ALaw;

        public bool IsMuLaw => _encoding == SampleEncoding.MuLaw;

        public static AuReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != ".snd")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid AU file: missing '.snd' magic");
                }

                var headerSize = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                if (headerSize < 24)
                {
                    throw new InvalidDataException($"'{filePath}' declares a header size of {headerSize} bytes, too small to hold AU's own 24-byte fixed header");
                }

                var declaredDataSize = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                var encodingValue = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                var sampleRate = (int)BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                var channels = (int)BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());

                if (channels <= 0)
                {
                    throw new InvalidDataException($"'{filePath}' declares {channels} channels in its header");
                }

                var (encoding, reportedBitsPerSample, bytesPerDiskSample) = DetermineEncoding(filePath, encodingValue);

                var sampleDataStart = headerSize; // the annotation string, if any, fills (headerSize - 24) and is never read
                var sampleDataLength = declaredDataSize == UnknownDataSize ? stream.Length - sampleDataStart : declaredDataSize;

                var bytesPerFrame = bytesPerDiskSample * channels;
                var totalSamples = sampleDataLength / bytesPerFrame;

                return new AuReader(stream, channels, sampleRate, reportedBitsPerSample, totalSamples, sampleDataStart, sampleDataLength, encoding, bytesPerDiskSample);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Maps an AU encoding value to this reader's internal SampleEncoding enum, the bitsPerSample
        // this reader reports publicly (the *decoded* resolution, not necessarily the coded width on
        // disk -- the same convention WavReader/AiffReader already use for their own ADPCM/G.711/float
        // variants), and the real on-disk byte width per sample.
        private static (SampleEncoding Encoding, int ReportedBitsPerSample, int BytesPerDiskSample) DetermineEncoding(string filePath, uint encodingValue)
        {
            return encodingValue switch
            {
                1 => (SampleEncoding.MuLaw, 16, 1),
                2 => (SampleEncoding.Signed8, 8, 1),
                3 => (SampleEncoding.Signed16, 16, 2),
                4 => (SampleEncoding.Signed24, 24, 3),
                5 => (SampleEncoding.Signed32, 32, 4),
                6 => (SampleEncoding.Float32, 32, 4),
                7 => (SampleEncoding.Float64, 32, 8),
                27 => (SampleEncoding.ALaw, 16, 1),
                _ => throw new NotSupportedException($"'{filePath}' uses unsupported AU encoding {encodingValue}")
            };
        }

        public int ReadInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var bytesPerSample = _bytesPerDiskSample;
            var bytesPerFrame = bytesPerSample * Channels;
            var remainingBytes = _sampleDataLength - _bytesRead;
            var framesToRead = (int)Math.Min(maxSamplesPerChannel, remainingBytes / bytesPerFrame);

            if (framesToRead <= 0)
            {
                return 0;
            }

            var byteCount = framesToRead * bytesPerFrame;
            if (_rawBytes.Length < byteCount)
            {
                _rawBytes = new byte[byteCount];
            }

            var bytesActuallyRead = ReadFully();
            _bytesRead += bytesActuallyRead;

            var sampleCount = bytesActuallyRead / bytesPerSample;
            for (var i = 0; i < sampleCount; i++)
            {
                var byteOffset = i * bytesPerSample;

                buffer[i] = _encoding switch
                {
                    SampleEncoding.Signed8 => unchecked((sbyte)_rawBytes[byteOffset]),
                    SampleEncoding.Signed16 => (short)((_rawBytes[byteOffset] << 8) | _rawBytes[byteOffset + 1]),
                    SampleEncoding.Signed24 => ((_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8)) >> 8,
                    SampleEncoding.Signed32 => (_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8) | _rawBytes[byteOffset + 3],
                    SampleEncoding.Float32 => Float32ToInt32(BinaryPrimitives.ReadSingleBigEndian(new ReadOnlySpan<byte>(_rawBytes, byteOffset, 4))),
                    SampleEncoding.Float64 => Float64ToInt32(BinaryPrimitives.ReadDoubleBigEndian(new ReadOnlySpan<byte>(_rawBytes, byteOffset, 8))),
                    SampleEncoding.ALaw => G711Codec.DecodeALaw(_rawBytes[byteOffset]),
                    SampleEncoding.MuLaw => G711Codec.DecodeMuLaw(_rawBytes[byteOffset]),
                    _ => throw new NotSupportedException($"Unsupported AU encoding: {_encoding}")
                };
            }

            return sampleCount / Channels;

            int ReadFully()
            {
                var totalBytesRead = 0;
                while (totalBytesRead < byteCount)
                {
                    var bytesReadThisCall = _stream.Read(_rawBytes, totalBytesRead, byteCount - totalBytesRead);
                    if (bytesReadThisCall == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesReadThisCall;
                }

                return totalBytesRead;
            }
        }

        private static int Float32ToInt32(float sample) => Pcm.FloatSampleConverter.ClampToNativeInt32(sample);

        // Same NaN/clamp/scale convention as Float32ToInt32, just taking a double directly rather than
        // narrowing to float first -- see AiffReader.Float64ToInt32's own comment for why (identical
        // reasoning: a 64-bit sample's whole reason to exist on disk is extra precision, so this
        // preserves it right up to the final int32-native-range scale).
        private static int Float64ToInt32(double sample)
        {
            if (double.IsNaN(sample))
            {
                return 0;
            }

            var clamped = Math.Clamp(sample, -1.0, 1.0);
            return (int)(clamped * int.MaxValue);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
