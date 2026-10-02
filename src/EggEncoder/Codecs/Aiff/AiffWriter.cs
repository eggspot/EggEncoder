using EggEncoder.Codecs;
using System.Buffers.Binary;
using System.Text;

namespace EggEncoder.Codecs.Aiff
{
    // Plain AIFF only (COMM/SSND, integer PCM) -- see AiffReader's doc comment for why AIFC isn't in
    // scope. Mirrors WavWriter structurally: same fixed-size-header-written-at-Create-time constraint
    // (so, like WavWriter, this needs an exact totalFrames up front -- AudioCutter.Pipeline.cs's
    // DeferredFixedHeaderSink pattern applies here too), same reused raw-byte scratch buffer, same per-bit-depth
    // write switch. The differences are AIFF's: big-endian throughout, an 80-bit extended-float sample
    // rate (see IeeeExtendedFloat), and signed (not WAV's unsigned) 8-bit samples.
    public sealed class AiffWriter : IAudioSink
    {
        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _needsPadByte;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private AiffWriter(FileStream stream, int channels, int bitsPerSample, bool needsPadByte)
        {
            _stream = stream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _needsPadByte = needsPadByte;
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32.</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the FORM/COMM/SSND chunk size fields are written at creation time.</param>
        public static AiffWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit integer PCM are supported");
            }

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            try
            {
                var bytesPerSample = bitsPerSample / 8;
                var blockAlign = channels * bytesPerSample;
                var dataSize = totalFrames * blockAlign;
                var needsPadByte = dataSize % 2 != 0;

                const int commChunkSize = 18; // channels(2) + numSampleFrames(4) + sampleSize(2) + sampleRate(10)
                var ssndChunkSize = 8 + dataSize; // offset(4) + blockSize(4) + data (SSND's own declared size never includes its pad byte, matching WavWriter's "data" chunk convention)
                var formSize = 4 + (8 + commChunkSize) + (8 + ssndChunkSize) + (needsPadByte ? 1 : 0); // 4 = "AIFF" form type; FORM's size *does* include the pad byte, matching WavWriter's RIFF size

                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

                writer.Write("FORM"u8);
                WriteUInt32BigEndian(writer, (uint)formSize);
                writer.Write("AIFF"u8);

                writer.Write("COMM"u8);
                WriteUInt32BigEndian(writer, commChunkSize);
                WriteInt16BigEndian(writer, (short)channels);
                WriteUInt32BigEndian(writer, (uint)totalFrames);
                WriteInt16BigEndian(writer, (short)bitsPerSample);
                Span<byte> sampleRateBytes = stackalloc byte[10];
                IeeeExtendedFloat.FromDouble(sampleRate, sampleRateBytes);
                writer.Write(sampleRateBytes);

                writer.Write("SSND"u8);
                WriteUInt32BigEndian(writer, (uint)ssndChunkSize);
                WriteUInt32BigEndian(writer, 0); // offset
                WriteUInt32BigEndian(writer, 0); // blockSize -- only meaningful for block-aligned compressed AIFC data

                return new AiffWriter(stream, channels, bitsPerSample, needsPadByte);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var bytesPerSample = _bitsPerSample / 8;
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

                // Big-endian, and signed even at 8-bit -- the exact mirror of AiffReader's decode switch.
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

            _stream.Write(_rawBytes, 0, byteCount);
        }

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

            if (_needsPadByte)
            {
                _stream.WriteByte(0);
            }

            _stream.Dispose();
        }

        private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            writer.Write(bytes);
        }

        private static void WriteInt16BigEndian(BinaryWriter writer, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            writer.Write(bytes);
        }
    }
}
