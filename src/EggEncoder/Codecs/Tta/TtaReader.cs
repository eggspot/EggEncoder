using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Tta
{
    // Reads a TTA (True Audio) file: a 22-byte little-endian header (with its own CRC32), a mandatory
    // seek table of one 4-byte frame-byte-size per frame (plus its own trailing CRC32), then each
    // frame's compressed bytes back to back (each ending with its own 4-byte CRC32 over just that
    // frame). Confirmed against ffmpeg's tta.c/libavformat/tta.c.
    internal sealed class TtaReader : IDisposable
    {
        private const int HeaderSize = 18; // everything except the header's own trailing CRC32
        private readonly FileStream _stream;
        private readonly int[] _frameByteSizes;

        private int _nextFrameIndex;

        private TtaReader(FileStream stream, int channels, int bitsPerSample, int sampleRate, long totalSamples, int frameLength, int[] frameByteSizes)
        {
            _stream = stream;
            _frameByteSizes = frameByteSizes;

            Channels = channels;
            BitsPerSample = bitsPerSample;
            SampleRate = sampleRate;
            TotalSamples = totalSamples;
            FrameLength = frameLength;
        }

        public int Channels { get; }

        public int BitsPerSample { get; }

        public int SampleRate { get; }

        public long TotalSamples { get; }

        public int FrameLength { get; }

        public bool HasMoreFrames => _nextFrameIndex < _frameByteSizes.Length;

        public static TtaReader Open(string filePath)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            try
            {
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                var header = reader.ReadBytes(HeaderSize);
                if (header.Length != HeaderSize || Encoding.ASCII.GetString(header, 0, 4) != "TTA1")
                {
                    throw new InvalidDataException($"'{filePath}' is not a valid TTA file: missing 'TTA1' header");
                }

                var headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(reader.ReadBytes(4));
                if (Crc32.Compute(header) != headerCrc)
                {
                    throw new InvalidDataException($"'{filePath}' has a TTA header that fails its own CRC32 check");
                }

                var format = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                if (format != 1)
                {
                    throw new NotSupportedException($"'{filePath}' uses TTA audio format {format}; only format 1 (PCM) is supported");
                }

                var channels = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
                var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
                var totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));

                if (channels == 0)
                {
                    throw new InvalidDataException($"'{filePath}' declares 0 channels");
                }

                var frameLength = (int)((long)sampleRate * 256 / 245);
                if (frameLength <= 0)
                {
                    throw new InvalidDataException($"'{filePath}' has a sample rate too small to compute a valid TTA frame length");
                }

                var totalFrames = totalSamples == 0 ? 0 : (int)((totalSamples + frameLength - 1) / frameLength);
                var frameByteSizes = ReadSeekTable(reader, filePath, totalFrames);

                return new TtaReader(stream, channels, bitsPerSample, sampleRate, totalSamples, frameLength, frameByteSizes);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Returns just the frame's compressed bitstream bytes, with the seek table's declared size
        // (which includes a trailing 4-byte CRC32 of that bitstream) already validated and stripped.
        public byte[] ReadNextFrame()
        {
            if (!HasMoreFrames)
            {
                throw new InvalidOperationException("No more TTA frames remain in this file");
            }

            var size = _frameByteSizes[_nextFrameIndex];
            if (size < 4)
            {
                throw new InvalidDataException($"TTA frame {_nextFrameIndex} has an impossible size ({size} bytes; must be at least 4 for its own trailing CRC32)");
            }

            var frameBytes = new byte[size];
            var bytesRead = _stream.Read(frameBytes, 0, size);

            if (bytesRead != size)
            {
                throw new InvalidDataException("TTA file ended before all frames listed in the seek table were read");
            }

            var dataLength = size - 4;
            var frameCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameBytes.AsSpan(dataLength));
            if (Crc32.Compute(frameBytes.AsSpan(0, dataLength)) != frameCrc)
            {
                throw new InvalidDataException($"TTA frame {_nextFrameIndex} fails its own CRC32 check");
            }

            _nextFrameIndex++;
            return frameBytes[..dataLength];
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private static int[] ReadSeekTable(BinaryReader reader, string filePath, int totalFrames)
        {
            var tableBytes = reader.ReadBytes(totalFrames * 4);
            if (tableBytes.Length != totalFrames * 4)
            {
                throw new InvalidDataException($"'{filePath}' is truncated: its seek table is incomplete");
            }

            var tableCrc = BinaryPrimitives.ReadUInt32LittleEndian(reader.ReadBytes(4));
            if (Crc32.Compute(tableBytes) != tableCrc)
            {
                throw new InvalidDataException($"'{filePath}' has a TTA seek table that fails its own CRC32 check");
            }

            var sizes = new int[totalFrames];
            for (var i = 0; i < totalFrames; i++)
            {
                sizes[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(i * 4));
            }

            return sizes;
        }
    }
}
