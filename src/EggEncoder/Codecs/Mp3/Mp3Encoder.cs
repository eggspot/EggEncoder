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

        /// <summary>The variable-bit-rate quality <see cref="EncodeVbr"/> and <see cref="OpenVbrSession"/> use by default.</summary>
        public const int DefaultVbrQuality = 4;

        private static readonly int[] VbrTargetGains = [138, 142, 146, 150, 154, 158, 162, 166, 170, 174];

        /// <summary>
        /// Encodes a WAV file to a variable-bit-rate MP3: each frame uses the lowest MPEG-1 Layer III bitrate (32 to
        /// 320 kb/s) that quantises it at least as finely as <paramref name="quality"/> asks for, and the first frame
        /// carries a Xing header with the frame count, byte count and a seek table.
        /// </summary>
        /// <param name="sourceWavFilePath">The WAV file to encode.</param>
        /// <param name="destMp3FilePath">The MP3 file to write.</param>
        /// <param name="quality">0 (best, largest) to 9 (smallest), like LAME's <c>-V</c> scale.</param>
        /// <param name="tags">ID3v2 text frames to write, or null.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 0-9.</exception>
        /// <exception cref="NotSupportedException">A sample rate other than 32000, 44100 or 48000 Hz, or more than two channels.</exception>
        public static void EncodeVbr(string sourceWavFilePath, string destMp3FilePath, int quality = DefaultVbrQuality, IReadOnlyDictionary<string, string>? tags = null)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = OpenVbrSession(destMp3FilePath, wavReader.Channels, wavReader.SampleRate, wavReader.BitsPerSample, quality, tags);
            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];
            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }

        /// <summary>Opens a variable-bit-rate MP3 encoding session; see <see cref="EncodeVbr"/>.</summary>
        /// <param name="destMp3FilePath">The MP3 file to write.</param>
        /// <param name="channels">1 or 2.</param>
        /// <param name="sampleRate">32000, 44100 or 48000 Hz.</param>
        /// <param name="bitsPerSample">8, 16, 24 or 32.</param>
        /// <param name="quality">0 (best, largest) to 9 (smallest), like LAME's <c>-V</c> scale.</param>
        /// <param name="tags">ID3v2 text frames to write, or null.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 0-9.</exception>
        /// <exception cref="NotSupportedException">A sample rate other than 32000, 44100 or 48000 Hz, or more than two channels.</exception>
        public static Mp3EncoderSession OpenVbrSession(string destMp3FilePath, int channels, int sampleRate, int bitsPerSample, int quality = DefaultVbrQuality, IReadOnlyDictionary<string, string>? tags = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(quality, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, VbrTargetGains.Length - 1);
            var tagBytes = tags is { Count: > 0 } ? Id3v2Writer.BuildTagBytes(tags) : null;
            var destStream = File.Create(destMp3FilePath);
            if (tagBytes is not null)
            {
                destStream.Write(tagBytes);
            }

            try
            {
                return new Mp3EncoderSession(destStream, channels, sampleRate, bitsPerSample, 128, VbrTargetGains[quality]);
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
        private readonly bool _variableBitRate;
        private readonly List<long> _frameOffsets = [];

        private int _audioFrameCount;
        private long _totalByteCount;
        private bool _finished;
        private bool _disposed;

        internal Mp3EncoderSession(FileStream destStream, int channels, int sampleRate, int bitsPerSample, int bitRateKbps, int? vbrTargetGain = null)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSampleShift = bitsPerSample - 16;
            _variableBitRate = vbrTargetGain is not null;
            _frameEncoder = new Mp3FrameEncoder(channels, sampleRate, bitRateKbps, vbrTargetGain);

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
            var finalInfoTagFrame = _frameEncoder.BuildInfoTagFrame(_audioFrameCount, (int)_totalByteCount, startPad, endPad, _variableBitRate ? BuildSeekTable() : null);

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

        private byte[] BuildSeekTable()
        {
            var table = new byte[100];
            for (var i = 0; i < table.Length; i++)
            {
                var frame = Math.Min(_frameOffsets.Count - 1, (int)((long)i * _frameOffsets.Count / 100));
                var offset = frame < 0 ? 0 : _frameOffsets[frame];
                table[i] = (byte)Math.Min(255, offset * 256 / Math.Max(1, _totalByteCount));
            }

            return table;
        }

        private void EncodeFrame()
        {
            _frameOffsets.Add(_totalByteCount);
            var frameBytes = _frameEncoder.EncodeFrame(CollectionsMarshal.AsSpan(_pendingInterleaved)[..(FrameSamples * _channels)]);
            _destStream.Write(frameBytes);
            _pendingInterleaved.RemoveRange(0, FrameSamples * _channels);
            _audioFrameCount++;
            _totalByteCount += frameBytes.Length;
        }
    }
}
