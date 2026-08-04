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
            if (bitsPerSample is not 16 and not 24)
            {
                throw new NotSupportedException($"'{filePath}' requests {bitsPerSample}-bit samples; only 16-bit and 24-bit PCM are supported");
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
            var rawBytes = new byte[sampleCount * bytesPerSample];

            for (var i = 0; i < sampleCount; i++)
            {
                var byteOffset = i * bytesPerSample;
                var sample = buffer[i];

                switch (bytesPerSample)
                {
                    case 2:
                        rawBytes[byteOffset] = (byte)sample;
                        rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        break;
                    case 3:
                        rawBytes[byteOffset] = (byte)sample;
                        rawBytes[byteOffset + 1] = (byte)(sample >> 8);
                        rawBytes[byteOffset + 2] = (byte)(sample >> 16);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported bytes per sample: {bytesPerSample}");
                }
            }

            _stream.Write(rawBytes, 0, rawBytes.Length);
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
