using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using EggEncoder.Native;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Encoder
    {
        public const int DefaultBitRateKbps = 320;
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destMp3FilePath, int bitRateKbps = DefaultBitRateKbps)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destMp3FilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, bitRateKbps);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        public static Mp3EncoderSession OpenSession(string destMp3FilePath, int channels, int sampleRate, int bitsPerSample, int bitRateKbps = DefaultBitRateKbps)
        {
            var config = new BeConfig
            {
                DwConfig = BeConfig.ConfigLame,
                DwStructVersion = 1,
                DwStructSize = 331,
                DwSampleRate = (uint)sampleRate,
                NMode = (int)(channels == 1 ? BeMp3Mode.Mono : BeMp3Mode.JointStereo),
                DwBitrate = (uint)bitRateKbps,
                NPreset = (int)BeQualityPreset.NoPreset,
                DwMpegVersion = 1,
                BOriginal = 1,
                BWriteVbrHeader = 1,
                NVbrMethod = (int)BeVbrMethod.None
            };

            uint samplesPerChunk = 0;
            var initResult = Mp3Native.BeInitStream(ref config, ref samplesPerChunk, out var mp3BufferSize, out var streamHandle);
            if (initResult != 0)
            {
                throw new InvalidOperationException($"Failed to initialize LAME encoder for '{destMp3FilePath}': error {initResult}");
            }

            FileStream? destStream = null;
            try
            {
                destStream = new FileStream(destMp3FilePath, FileMode.Create, FileAccess.Write);

                return new Mp3EncoderSession(streamHandle, channels, bitsPerSample - 16, samplesPerChunk, mp3BufferSize, destStream, destMp3FilePath);
            }
            catch
            {
                destStream?.Dispose();
                Mp3Native.BeCloseStream(streamHandle);
                throw;
            }
        }
    }

    public sealed class Mp3EncoderSession : IAudioSink
    {
        private readonly IntPtr _streamHandle;
        private readonly int _channels;
        private readonly int _bitsPerSampleShift;
        private readonly uint _samplesPerChunk;
        private readonly short[] _pendingSamples;
        private readonly byte[] _mp3Buffer;
        private readonly FileStream _destStream;
        private readonly string _destMp3FilePath;

        private int _pendingSampleCount;
        private bool _disposed;

        internal Mp3EncoderSession(IntPtr streamHandle, int channels, int bitsPerSampleShift, uint samplesPerChunk, uint mp3BufferSize, FileStream destStream, string destMp3FilePath)
        {
            _streamHandle = streamHandle;
            _channels = channels;
            _bitsPerSampleShift = bitsPerSampleShift;
            _samplesPerChunk = samplesPerChunk;
            _pendingSamples = new short[samplesPerChunk];
            _mp3Buffer = new byte[mp3BufferSize];
            _destStream = destStream;
            _destMp3FilePath = destMp3FilePath;
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var sampleCount = frameCount * _channels;
            var sampleIndex = 0;

            while (sampleIndex < sampleCount)
            {
                var samplesToCopy = Math.Min(sampleCount - sampleIndex, (int)_samplesPerChunk - _pendingSampleCount);
                for (var i = 0; i < samplesToCopy; i++)
                {
                    var sample = buffer[sampleIndex + i];
                    _pendingSamples[_pendingSampleCount + i] = (short)(_bitsPerSampleShift > 0 ? sample >> _bitsPerSampleShift : sample);
                }

                _pendingSampleCount += samplesToCopy;
                sampleIndex += samplesToCopy;

                if (_pendingSampleCount == _samplesPerChunk)
                {
                    EncodeChunk((uint)_pendingSampleCount);
                    _pendingSampleCount = 0;
                }
            }
        }

        public void Finish()
        {
            if (_pendingSampleCount > 0)
            {
                EncodeChunk((uint)_pendingSampleCount);
                _pendingSampleCount = 0;
            }

            var deinitResult = Mp3Native.BeDeinitStream(_streamHandle, _mp3Buffer, out var flushBytesWritten);
            if (deinitResult != 0)
            {
                throw new InvalidOperationException($"LAME encoder failed to flush '{_destMp3FilePath}': error {deinitResult}");
            }

            _destStream.Write(_mp3Buffer, 0, (int)flushBytesWritten);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Mp3Native.BeCloseStream(_streamHandle);
            _destStream.Dispose();
        }

        private void EncodeChunk(uint sampleCount)
        {
            var encodeResult = Mp3Native.BeEncodeChunk(_streamHandle, sampleCount, _pendingSamples, _mp3Buffer, out var bytesWritten);
            if (encodeResult != 0)
            {
                throw new InvalidOperationException($"LAME encoder failed to process samples for '{_destMp3FilePath}': error {encodeResult}");
            }

            _destStream.Write(_mp3Buffer, 0, (int)bytesWritten);
        }
    }
}
