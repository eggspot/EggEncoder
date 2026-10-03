namespace EggEncoder.Codecs.Tta
{
    // Encodes 1 or 2 channels of 16-bit PCM into a TTA file -- see TtaDecoder's doc comment for scope.
    // Samples are buffered per TTA frame (sampleRate*256/245 frames; the final frame may be shorter)
    // and encoded into frames as each fills; the frames themselves (compressed, far smaller than the
    // raw PCM Mix already buffers in full) are held in memory until Finish(), since TtaWriter's seek
    // table needs every frame's final compressed size up front.
    public sealed class TtaEncoderSession : IAudioSink
    {
        private readonly FileStream _destStream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _sampleRate;
        private readonly int _frameLength;
        private readonly List<byte[]> _frames = [];
        private readonly int[] _pendingSamples;

        private int _pendingCount;
        private long _totalSamples;
        private bool _disposed;

        private TtaEncoderSession(FileStream destStream, int channels, int bitsPerSample, int sampleRate)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _sampleRate = sampleRate;
            _frameLength = (int)((long)sampleRate * 256 / 245);

            _pendingSamples = new int[_frameLength * channels];
        }

        public static TtaEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo TTA encoding is supported");
            }

            if (bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit TTA encoding is supported");
            }

            if (sampleRate <= 0)
            {
                throw new NotSupportedException($"'{destFilePath}' requests a sample rate of {sampleRate}; only positive sample rates are supported for TTA encoding");
            }

            var destStream = File.Create(destFilePath);
            try
            {
                return new TtaEncoderSession(destStream, channels, bitsPerSample, sampleRate);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            var sampleCount = frameCount * _channels;
            var bufferOffset = 0;

            while (bufferOffset < sampleCount)
            {
                var pendingFrameCount = _pendingCount / _channels;
                var samplesAvailableThisFrame = (_frameLength - pendingFrameCount) * _channels;
                var samplesToCopy = Math.Min(samplesAvailableThisFrame, sampleCount - bufferOffset);

                Array.Copy(buffer, bufferOffset, _pendingSamples, _pendingCount, samplesToCopy);
                _pendingCount += samplesToCopy;
                bufferOffset += samplesToCopy;

                if (_pendingCount == _pendingSamples.Length)
                {
                    FlushPendingFrame();
                }
            }

            _totalSamples += frameCount;
        }

        public void Finish()
        {
            if (_pendingCount > 0)
            {
                FlushPendingFrame();
            }

            TtaWriter.Write(_destStream, _channels, _bitsPerSample, _sampleRate, _totalSamples, _frames);
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

        private void FlushPendingFrame()
        {
            // _pendingSamples may be longer than _pendingCount for a short final frame; EncodeFrame is
            // bounded by the sampleCount argument, never the array length, so passing the live buffer
            // directly is safe -- it's fully consumed synchronously before this returns.
            var sampleCount = _pendingCount / _channels;
            _frames.Add(TtaFrameEncoder.EncodeFrame(_pendingSamples, _channels, sampleCount));
            _pendingCount = 0;
        }
    }
}
