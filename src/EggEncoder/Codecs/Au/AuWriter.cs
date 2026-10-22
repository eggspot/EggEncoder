using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Au
{
    // Sun/NeXT AU (.au) writer -- see AuReader's doc comment for the full encoding coverage and header
    // layout. Always writes the minimal 24-byte header with no annotation string and an exact dataSize
    // (AuSampleFormat.Integer's bitsPerSample picks which of encodings 2/3/4/5 to use; every other
    // value selects 1/6/7/27). Unlike WavWriter/AiffWriter, there's no IFF-style even-byte chunk
    // alignment to account for -- AU has no pad byte at all (confirmed against a real ffmpeg-produced
    // fixture whose data region ends at an odd byte offset), so Dispose has nothing to patch.
    public sealed class AuWriter : IAudioSink
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly int _channels;
        private readonly int _bytesPerDiskSample;
        private readonly AuSampleFormat _sampleFormat;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private AuWriter(Stream stream, bool leaveOpen, int channels, int bytesPerDiskSample, AuSampleFormat sampleFormat)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
            _channels = channels;
            _bytesPerDiskSample = bytesPerDiskSample;
            _sampleFormat = sampleFormat;
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32.</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the header's own dataSize field is written at creation time (AU's own "unknown size" sentinel exists for a real streaming encoder, but this writer never needs it, the same way WavWriter/AiffWriter never need WAV/AIFF's absence of an equivalent sentinel).</param>
        public static AuWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            return Create(filePath, channels, sampleRate, bitsPerSample, totalFrames, AuSampleFormat.Integer);
        }

        /// <summary>
        /// Creates an AU destination from an already-open <see cref="Stream"/> instead of a file path --
        /// for a destination that isn't a local file (an HTTP request body, blob storage, an in-memory
        /// buffer). No <see cref="Stream.CanSeek"/> requirement, the same reasoning as
        /// <c>Wav.WavWriter.Create(Stream, ...)</c>'s own doc comment: the header's own dataSize field
        /// is already known from <paramref name="totalFrames"/> up front, so this writes the complete
        /// header once and never seeks back. Does NOT dispose <paramref name="destStream"/> -- the
        /// caller opened it, so the caller is responsible for disposing it.
        /// </summary>
        public static AuWriter Create(Stream destStream, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            return Create(destStream, channels, sampleRate, bitsPerSample, totalFrames, AuSampleFormat.Integer);
        }

        /// <summary>
        /// Same as <see cref="Create(string, int, int, int, long)"/>, but additionally selects the
        /// on-disk sample representation (see <see cref="AuSampleFormat"/>).
        /// </summary>
        public static AuWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames, AuSampleFormat sampleFormat)
        {
            ValidateChannelsAndSampleRate(channels, sampleRate);
            var (encodingValue, bytesPerDiskSample) = DetermineEncoding(filePath, sampleFormat, bitsPerSample);

            // No outer try/catch needed here -- WriteAndCreate's own catch already disposes stream
            // (since leaveOpen is false) before rethrowing.
            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            return WriteAndCreate(stream, leaveOpen: false, channels, sampleRate, totalFrames, sampleFormat, encodingValue, bytesPerDiskSample);
        }

        /// <summary>
        /// Same as <see cref="Create(string, int, int, int, long, AuSampleFormat)"/>, but from an
        /// already-open <see cref="Stream"/> -- see the four-parameter
        /// <see cref="Create(Stream, int, int, int, long)"/> overload's own doc comment for the
        /// ownership/seekability rationale.
        /// </summary>
        public static AuWriter Create(Stream destStream, int channels, int sampleRate, int bitsPerSample, long totalFrames, AuSampleFormat sampleFormat)
        {
            ArgumentNullException.ThrowIfNull(destStream);
            ValidateChannelsAndSampleRate(channels, sampleRate);

            var (encodingValue, bytesPerDiskSample) = DetermineEncoding("<stream>", sampleFormat, bitsPerSample);

            return WriteAndCreate(destStream, leaveOpen: true, channels, sampleRate, totalFrames, sampleFormat, encodingValue, bytesPerDiskSample);
        }

        // The same limits AuReader.Open enforces on read, checked before the destination file is
        // even created -- so this writer can never produce a file this project's own reader rejects.
        private static void ValidateChannelsAndSampleRate(int channels, int sampleRate)
        {
            if (channels is < 1 or > AuReader.MaxChannels)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), channels, $"Channel count must be 1-{AuReader.MaxChannels}");
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive");
            }
        }

        private static AuWriter WriteAndCreate(Stream stream, bool leaveOpen, int channels, int sampleRate, long totalFrames, AuSampleFormat sampleFormat, uint encodingValue, int bytesPerDiskSample)
        {
            try
            {
                var blockAlign = channels * bytesPerDiskSample;
                var dataSize = totalFrames * blockAlign;

                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

                writer.Write(".snd"u8);
                WriteUInt32BigEndian(writer, 24); // headerSize -- no annotation string written
                WriteUInt32BigEndian(writer, (uint)dataSize);
                WriteUInt32BigEndian(writer, encodingValue);
                WriteUInt32BigEndian(writer, (uint)sampleRate);
                WriteUInt32BigEndian(writer, (uint)channels);

                return new AuWriter(stream, leaveOpen, channels, bytesPerDiskSample, sampleFormat);
            }
            catch
            {
                if (!leaveOpen)
                {
                    stream.Dispose();
                }

                throw;
            }
        }

        // Maps (sampleFormat, bitsPerSample) to the AU encoding value to write and the real on-disk
        // byte width per sample. AuSampleFormat.Integer picks among 2/3/4/5 by bitsPerSample; every
        // other value is a single fixed encoding.
        private static (uint EncodingValue, int BytesPerDiskSample) DetermineEncoding(string filePath, AuSampleFormat sampleFormat, int bitsPerSample)
        {
            switch (sampleFormat)
            {
                case AuSampleFormat.Integer:
                    return bitsPerSample switch
                    {
                        8 => (2u, 1),
                        16 => (3u, 2),
                        24 => (4u, 3),
                        32 => (5u, 4),
                        _ => throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit integer PCM are supported")
                    };

                case AuSampleFormat.Float32:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 32);
                    return (6u, 4);

                case AuSampleFormat.Float64:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 32);
                    return (7u, 8);

                case AuSampleFormat.MuLaw:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 16);
                    return (1u, 1);

                case AuSampleFormat.ALaw:
                    RequireBitsPerSample(filePath, sampleFormat, bitsPerSample, required: 16);
                    return (27u, 1);

                default:
                    throw new NotSupportedException($"Unsupported {nameof(AuSampleFormat)}: {sampleFormat}");
            }
        }

        private static void RequireBitsPerSample(string filePath, AuSampleFormat sampleFormat, int bitsPerSample, int required)
        {
            if (bitsPerSample != required)
            {
                throw new NotSupportedException($"'{filePath}' requests {nameof(AuSampleFormat)}.{sampleFormat} but {bitsPerSample}-bit samples; only {required}-bit is valid");
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var bytesPerSample = _bytesPerDiskSample;
            var sampleCount = frameCount * _channels;
            var byteCount = sampleCount * bytesPerSample;

            if (_rawBytes.Length < byteCount)
            {
                _rawBytes = new byte[byteCount];
            }

            for (var i = 0; i < sampleCount; i++)
            {
                var byteOffset = i * bytesPerSample;
                var sample = buffer[i];

                switch (_sampleFormat)
                {
                    case AuSampleFormat.Integer:
                        WriteBigEndianInteger(byteOffset, bytesPerSample, sample);
                        break;
                    case AuSampleFormat.Float32:
                        BinaryPrimitives.WriteSingleBigEndian(new Span<byte>(_rawBytes, byteOffset, 4), Int32ToFloat32(sample));
                        break;
                    case AuSampleFormat.Float64:
                        BinaryPrimitives.WriteDoubleBigEndian(new Span<byte>(_rawBytes, byteOffset, 8), Int32ToFloat64(sample));
                        break;
                    case AuSampleFormat.ALaw:
                        _rawBytes[byteOffset] = G711Codec.EncodeALaw(sample);
                        break;
                    case AuSampleFormat.MuLaw:
                        _rawBytes[byteOffset] = G711Codec.EncodeMuLaw(sample);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported {nameof(AuSampleFormat)}: {_sampleFormat}");
                }
            }

            _stream.Write(_rawBytes, 0, byteCount);
        }

        // Big-endian, and signed even at 8-bit -- mirrors AiffWriter's own WriteBigEndianInteger exactly
        // (AU shares the same convention), kept as its own copy rather than a shared helper so this
        // format's own correctness doesn't depend on not regressing AIFF's already-reviewed code.
        private void WriteBigEndianInteger(int byteOffset, int bytesPerSample, int sample)
        {
            switch (bytesPerSample)
            {
                case 1:
                    _rawBytes[byteOffset] = unchecked((byte)sample);
                    break;
                case 2:
                    _rawBytes[byteOffset] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 1] = (byte)sample;
                    break;
                case 3:
                    _rawBytes[byteOffset] = (byte)(sample >> 16);
                    _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 2] = (byte)sample;
                    break;
                case 4:
                    _rawBytes[byteOffset] = (byte)(sample >> 24);
                    _rawBytes[byteOffset + 1] = (byte)(sample >> 16);
                    _rawBytes[byteOffset + 2] = (byte)(sample >> 8);
                    _rawBytes[byteOffset + 3] = (byte)sample;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
            }
        }

        private static float Int32ToFloat32(int sample) => (float)(sample / (double)int.MaxValue);

        private static double Int32ToFloat64(int sample) => sample / (double)int.MaxValue;

        public void Finish()
        {
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
