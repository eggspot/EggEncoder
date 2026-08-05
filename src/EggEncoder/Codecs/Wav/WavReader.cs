using System.Text;

namespace EggEncoder.Codecs.Wav
{
    public sealed class WavReader : IDisposable
    {
        private const int PcmFormatTag = 1;
        private const int IeeeFloatFormatTag = 3;
        private const int WaveFormatExtensibleTag = 0xFFFE;

        private readonly FileStream _stream;
        private readonly long _dataChunkLength;
        private readonly bool _isFloatFormat;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        private WavReader(FileStream stream, int channels, int sampleRate, int bitsPerSample, bool isFloatFormat, long dataChunkStart, long dataChunkLength)
        {
            _stream = stream;
            _dataChunkLength = dataChunkLength;
            _isFloatFormat = isFloatFormat;

            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            TotalSamples = dataChunkLength / (channels * (bitsPerSample / 8));

            _stream.Seek(dataChunkStart, SeekOrigin.Begin);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public int BitsPerSample { get; }

        public long TotalSamples { get; }

        public static WavReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "RIFF")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid WAV file: missing RIFF header");
                }

                reader.ReadUInt32();
                if (new string(reader.ReadChars(4)) != "WAVE")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid WAV file: missing WAVE header");
                }

                int? channels = null;
                int? sampleRate = null;
                int? bitsPerSample = null;
                var isFloatFormat = false;
                long dataChunkStart = 0;
                long dataChunkLength = 0;
                var dataChunkFound = false;

                while (stream.Position < stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = reader.ReadUInt32();
                    var chunkDataStart = stream.Position;

                    if (chunkId == "fmt ")
                    {
                        var formatTag = reader.ReadUInt16();
                        if (formatTag != PcmFormatTag && formatTag != IeeeFloatFormatTag && formatTag != WaveFormatExtensibleTag)
                        {
                            throw new NotSupportedException($"'{filePath}' uses unsupported WAV format tag {formatTag}; only PCM and IEEE float are supported");
                        }

                        isFloatFormat = formatTag == IeeeFloatFormatTag;

                        channels = reader.ReadUInt16();
                        sampleRate = (int)reader.ReadUInt32();
                        reader.ReadUInt32();
                        reader.ReadUInt16();
                        bitsPerSample = reader.ReadUInt16();
                    }
                    else if (chunkId == "data")
                    {
                        dataChunkStart = chunkDataStart;
                        dataChunkLength = chunkSize;
                        dataChunkFound = true;
                    }

                    var paddedChunkSize = chunkSize + (chunkSize % 2);
                    stream.Position = chunkDataStart + paddedChunkSize;
                }

                if (channels is null || sampleRate is null || bitsPerSample is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'fmt ' chunk");
                }

                if (!dataChunkFound)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'data' chunk");
                }

                if (isFloatFormat)
                {
                    if (bitsPerSample != 32)
                    {
                        throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit IEEE float samples; only 32-bit IEEE float is supported");
                    }
                }
                else if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
                {
                    throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit PCM are supported");
                }

                return new WavReader(stream, channels.Value, sampleRate.Value, bitsPerSample.Value, isFloatFormat, dataChunkStart, dataChunkLength);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public int ReadInterleavedSamples(int[] buffer, int maxSamplesPerChannel)
        {
            var bytesPerSample = BitsPerSample / 8;
            var bytesPerFrame = bytesPerSample * Channels;
            var remainingBytes = _dataChunkLength - _bytesRead;
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
                buffer[i] = bytesPerSample switch
                {
                    1 => _rawBytes[byteOffset] - 128,
                    2 => (short)(_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8)),
                    3 => (_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16)) << 8 >> 8,
                    4 => _isFloatFormat
                        ? Float32ToInt32(BitConverter.Int32BitsToSingle(_rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16) | (_rawBytes[byteOffset + 3] << 24)))
                        : _rawBytes[byteOffset] | (_rawBytes[byteOffset + 1] << 8) | (_rawBytes[byteOffset + 2] << 16) | (_rawBytes[byteOffset + 3] << 24),
                    _ => throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}")
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

        private static int Float32ToInt32(float sample)
        {
            // Widen to double before scaling: int.MaxValue isn't exactly representable as a float
            // (it rounds up to 2^31), which would overflow the cast back to int for sample == 1f.
            var clamped = Math.Clamp((double)sample, -1.0, 1.0);
            return (int)(clamped * int.MaxValue);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
