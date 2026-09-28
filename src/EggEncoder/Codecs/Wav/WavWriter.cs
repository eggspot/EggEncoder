using EggEncoder.Codecs;
using System.Text;

namespace EggEncoder.Codecs.Wav
{
    public sealed class WavWriter : IAudioSink
    {
        private const int PcmFormatTag = 1;
        private const int IeeeFloatFormatTag = 3;

        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _isFloatFormat;
        private readonly bool _needsPadByte;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private WavWriter(FileStream stream, int channels, int bitsPerSample, bool isFloatFormat, bool needsPadByte)
        {
            _stream = stream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _isFloatFormat = isFloatFormat;
            _needsPadByte = needsPadByte;
        }

        /// <param name="filePath">Destination path.</param>
        /// <param name="channels">Number of interleaved channels.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="bitsPerSample">Bit depth: 8, 16, 24, or 32 (must be 32 if <paramref name="isFloatFormat"/> is true).</param>
        /// <param name="totalFrames">Exact total frame count that will be written -- required up front since the RIFF header's size fields are written at creation time.</param>
        /// <param name="isFloatFormat">
        /// When true, writes a 32-bit IEEE float WAV instead of integer PCM: each incoming sample is
        /// still an int at this codebase's 32-bit native range (the same scale <c>WavReader</c> produces
        /// when it decodes a float WAV -- see <c>WavReader.Float32ToInt32</c> -- and the same scale
        /// <see cref="EggEncoder.Pcm.FloatSampleConverter"/> uses), converted to an actual IEEE 754 float
        /// at write time. Only valid with <paramref name="bitsPerSample"/> == 32.
        /// </param>
        public static WavWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames, bool isFloatFormat = false)
        {
            if (isFloatFormat && bitsPerSample != 32)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit IEEE float samples; only 32-bit IEEE float is supported");
            }

            if (!isFloatFormat && bitsPerSample is not 8 and not 16 and not 24 and not 32)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 8-bit, 16-bit, 24-bit, and 32-bit PCM are supported");
            }

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            try
            {
                var bytesPerSample = bitsPerSample / 8;
                var blockAlign = channels * bytesPerSample;
                var dataSize = totalFrames * blockAlign;
                var needsPadByte = dataSize % 2 != 0;

                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

                writer.Write("RIFF"u8);
                writer.Write((uint)(36 + dataSize + (needsPadByte ? 1 : 0)));
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write((uint)16);
                writer.Write((ushort)(isFloatFormat ? IeeeFloatFormatTag : PcmFormatTag));
                writer.Write((ushort)channels);
                writer.Write((uint)sampleRate);
                writer.Write((uint)(sampleRate * blockAlign));
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)bitsPerSample);
                writer.Write("data"u8);
                writer.Write((uint)dataSize);

                return new WavWriter(stream, channels, bitsPerSample, isFloatFormat, needsPadByte);
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

                switch (bytesPerSample)
                {
                    case 1:
                        _rawBytes[byteOffset] = (byte)(sample + 128);
                        break;
                    case 2:
                        _rawBytes[byteOffset] = (byte)sample;
                        _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        break;
                    case 3:
                        _rawBytes[byteOffset] = (byte)sample;
                        _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        _rawBytes[byteOffset + 2] = (byte)(sample >> 16);
                        break;
                    case 4:
                        var bits = _isFloatFormat ? BitConverter.SingleToInt32Bits(Int32ToFloat32(sample)) : sample;
                        _rawBytes[byteOffset] = (byte)bits;
                        _rawBytes[byteOffset + 1] = (byte)(bits >> 8);
                        _rawBytes[byteOffset + 2] = (byte)(bits >> 16);
                        _rawBytes[byteOffset + 3] = (byte)(bits >> 24);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                }
            }

            _stream.Write(_rawBytes, 0, byteCount);
        }

        // Exact inverse of WavReader.Float32ToInt32: a sample at this codebase's 32-bit int native
        // range maps back onto the same -1.0..1.0 normalized float WavReader would have produced it from.
        private static float Int32ToFloat32(int sample) => (float)(sample / (double)int.MaxValue);

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
    }
}
