using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Aiff
{
    // Plain AIFF only (COMM/SSND, integer PCM) -- not AIFC (compressed/float AIFF variants), which is a
    // different form-type ("AIFC") with a compression-type field in COMM and a version chunk; out of
    // scope here the same way WavReader doesn't need to handle anything beyond PCM/IEEE-float WAV.
    //
    // Structurally this mirrors WavReader closely: same two-pass (scan all chunks, then seek back to the
    // sample data start) approach, same reused raw-byte scratch buffer. The differences are exactly
    // AIFF's: big-endian multi-byte fields throughout (IFF, like RIFF, but the opposite byte order), an
    // 80-bit extended-float sample rate instead of a plain integer (see IeeeExtendedFloat), and -- easy to
    // get backwards -- AIFF's 8-bit samples are signed (-128..127), unlike WAV's unsigned (0..255) 8-bit
    // convention.
    public sealed class AiffReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly long _sampleDataLength;

        private long _bytesRead;
        private byte[] _rawBytes = [];

        private AiffReader(FileStream stream, int channels, int sampleRate, int bitsPerSample, long totalSamples, long sampleDataStart, long sampleDataLength)
        {
            _stream = stream;
            _sampleDataLength = sampleDataLength;

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

        public static AiffReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (new string(reader.ReadChars(4)) != "FORM")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid AIFF file: missing FORM header");
                }

                reader.ReadUInt32(); // FORM size -- not needed, every chunk carries its own size
                if (new string(reader.ReadChars(4)) != "AIFF")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid AIFF file: missing AIFF form type (AIFC is not supported)");
                }

                int? channels = null;
                long? totalSampleFrames = null;
                int? bitsPerSample = null;
                int? sampleRate = null;
                long sampleDataStart = 0;
                long sampleDataLength = 0;
                var ssndFound = false;

                while (stream.Position + 8 <= stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                    var chunkDataStart = stream.Position;

                    if (chunkId == "COMM")
                    {
                        channels = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());
                        totalSampleFrames = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                        bitsPerSample = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());
                        sampleRate = (int)Math.Round(IeeeExtendedFloat.ToDouble(reader.ReadBytes(10)));
                    }
                    else if (chunkId == "SSND")
                    {
                        var offset = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                        reader.ReadUInt32(); // blockSize -- only meaningful for block-aligned compressed AIFC data, unused for plain AIFF
                        sampleDataStart = chunkDataStart + 8 + offset;
                        sampleDataLength = chunkSize - 8 - offset;
                        ssndFound = true;
                    }

                    var paddedChunkSize = chunkSize + (chunkSize % 2);
                    stream.Position = chunkDataStart + paddedChunkSize;
                }

                if (channels is null || totalSampleFrames is null || bitsPerSample is null || sampleRate is null)
                {
                    throw new InvalidDataException($"'{filePath}' is missing a 'COMM' chunk");
                }

                if (!ssndFound)
                {
                    throw new InvalidDataException($"'{filePath}' is missing an 'SSND' chunk");
                }

                if (channels.Value <= 0)
                {
                    throw new InvalidDataException($"'{filePath}' declares {channels.Value} channels in its COMM chunk");
                }

                if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
                {
                    throw new NotSupportedException($"'{filePath}' has {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit integer PCM are supported");
                }

                return new AiffReader(stream, channels.Value, sampleRate.Value, bitsPerSample.Value, totalSampleFrames.Value, sampleDataStart, sampleDataLength);
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

                // Every AIFF bit depth is signed and big-endian -- including 8-bit, unlike WAV's unsigned
                // 8-bit convention. Each case sign-extends by composing big-endian bytes into the correct
                // native-width signed type before widening to int, rather than WAV's little-endian byte
                // order (lowest byte first).
                buffer[i] = bytesPerSample switch
                {
                    1 => unchecked((sbyte)_rawBytes[byteOffset]),
                    2 => (short)((_rawBytes[byteOffset] << 8) | _rawBytes[byteOffset + 1]),
                    3 => ((_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8)) >> 8,
                    4 => (_rawBytes[byteOffset] << 24) | (_rawBytes[byteOffset + 1] << 16) | (_rawBytes[byteOffset + 2] << 8) | _rawBytes[byteOffset + 3],
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

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
