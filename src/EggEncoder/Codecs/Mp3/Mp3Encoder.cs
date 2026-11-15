using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Encoder
    {
        public const int DefaultBitRateKbps = 320;
        private const int FramesPerBlock = 4096;

        /// <summary>
        /// The encoder delay, in samples, written into the "LAME tag" encoder-delay field of every
        /// <see cref="OpenSession"/> output. It counts audio frames only: decoders skip the Info-tag
        /// frame itself, then skip this delay plus their own 529-sample decoder delay, so a decoded
        /// file lines up with its source 1057 samples after the first audio frame starts.
        /// </summary>
        public const int EncoderDelaySamples = 528;

        public static void Encode(string sourceWavFilePath, string destMp3FilePath, int bitRateKbps = DefaultBitRateKbps, IReadOnlyDictionary<string, string>? tags = null)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenSession(destMp3FilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, bitRateKbps, tags);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        /// <summary>
        /// Opens an MP3 encoding session. MPEG-1 Layer III takes 32000, 44100 or 48000 Hz input only;
        /// any other rate throws <see cref="NotSupportedException"/>. <see cref="NativeEncoder"/> and
        /// <see cref="AudioCutter"/> conversions to MP3 resample other rates automatically
        /// (to 44100 Hz for multiples of 11025 Hz, otherwise to 48000 Hz).
        /// </summary>
        public static Mp3EncoderSession OpenSession(string destMp3FilePath, int channels, int sampleRate, int bitsPerSample, int bitRateKbps = DefaultBitRateKbps, IReadOnlyDictionary<string, string>? tags = null)
        {
            var tagBytes = tags is { Count: > 0 } ? Id3v2Writer.BuildTagBytes(tags) : null;

            var destStream = File.Create(destMp3FilePath);
            if (tagBytes is not null)
            {
                destStream.Write(tagBytes);
            }

            try
            {
                return new Mp3EncoderSession(destStream, channels, sampleRate, bitsPerSample, bitRateKbps);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        internal static int ChooseEncodedSampleRate(int sourceSampleRate)
        {
            if (sourceSampleRate is 32000 or 44100 or 48000)
            {
                return sourceSampleRate;
            }

            return sourceSampleRate % 11025 == 0 ? 44100 : 48000;
        }
    }

    public sealed class Mp3EncoderSession : IAudioSink
    {
        private const int FrameSamples = 1152;

        private readonly FileStream _destStream;
        private readonly int _channels;
        private readonly int _bitsPerSampleShift;
        private readonly Mp3FrameEncoder _frameEncoder;
        private readonly List<int> _pendingInterleaved = [];
        private readonly long _infoTagFrameOffset;

        private int _audioFrameCount;
        private long _totalByteCount;
        private bool _finished;
        private bool _disposed;

        internal Mp3EncoderSession(FileStream destStream, int channels, int sampleRate, int bitsPerSample, int bitRateKbps)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSampleShift = bitsPerSample - 16;
            _frameEncoder = new Mp3FrameEncoder(channels, sampleRate, bitRateKbps);

            _infoTagFrameOffset = destStream.Position;
            var placeholderInfoTagFrame = _frameEncoder.BuildInfoTagFrame(frameCount: 0, byteCount: 0, startPadSamples: 0, endPadSamples: 0);
            _destStream.Write(placeholderInfoTagFrame);
            _totalByteCount = placeholderInfoTagFrame.Length;
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            var sampleCount = frameCount * _channels;
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = buffer[i];
                _pendingInterleaved.Add(_bitsPerSampleShift >= 0 ? sample >> _bitsPerSampleShift : sample << -_bitsPerSampleShift);
            }

            while (_pendingInterleaved.Count >= FrameSamples * _channels)
            {
                EncodeFrame();
            }
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            var remainingFrames = _pendingInterleaved.Count / _channels;
            var naturalPaddingFrames = remainingFrames > 0 ? FrameSamples - remainingFrames : 0;
            if (naturalPaddingFrames > 0)
            {
                for (var i = 0; i < naturalPaddingFrames * _channels; i++)
                {
                    _pendingInterleaved.Add(0);
                }

                EncodeFrame();
            }

            for (var i = 0; i < FrameSamples * _channels; i++)
            {
                _pendingInterleaved.Add(0);
            }

            EncodeFrame();

            const int flushFrameSamples = FrameSamples;
            var startPad = Mp3Encoder.EncoderDelaySamples;
            var endPad = naturalPaddingFrames + flushFrameSamples - startPad;
            var finalInfoTagFrame = _frameEncoder.BuildInfoTagFrame(_audioFrameCount, (int)_totalByteCount, startPad, endPad);

            _destStream.Seek(_infoTagFrameOffset, SeekOrigin.Begin);
            _destStream.Write(finalInfoTagFrame);
            _destStream.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _destStream.Dispose();
        }

        private void EncodeFrame()
        {
            var frameBytes = _frameEncoder.EncodeFrame(CollectionsMarshal.AsSpan(_pendingInterleaved)[..(FrameSamples * _channels)]);
            _destStream.Write(frameBytes);
            _pendingInterleaved.RemoveRange(0, FrameSamples * _channels);
            _audioFrameCount++;
            _totalByteCount += frameBytes.Length;
        }
    }
}
