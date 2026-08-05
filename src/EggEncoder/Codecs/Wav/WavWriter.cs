using EggEncoder.Codecs;
using System.Text;

namespace EggEncoder.Codecs.Wav
{
    public sealed class WavWriter : IAudioSink
    {
        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _needsPadByte;

        private byte[] _rawBytes = [];
        private bool _disposed;

        private WavWriter(FileStream stream, int channels, int bitsPerSample, bool needsPadByte)
        {
            _stream = stream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _needsPadByte = needsPadByte;
        }

        public static WavWriter Create(string filePath, int channels, int sampleRate, int bitsPerSample, long totalFrames)
        {
            if (bitsPerSample is not 8 and not 16 and not 24 and not 32)
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
                writer.Write((ushort)1);
                writer.Write((ushort)channels);
                writer.Write((uint)sampleRate);
                writer.Write((uint)(sampleRate * blockAlign));
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)bitsPerSample);
                writer.Write("data"u8);
                writer.Write((uint)dataSize);

                return new WavWriter(stream, channels, bitsPerSample, needsPadByte);
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
                        _rawBytes[byteOffset] = (byte)sample;
                        _rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        _rawBytes[byteOffset + 2] = (byte)(sample >> 16);
                        _rawBytes[byteOffset + 3] = (byte)(sample >> 24);
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
    }
}
