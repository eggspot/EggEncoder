namespace EggEncoder.Codecs.Wma
{
    // Encodes to WMAv2/ASF. Unlike the other streaming sessions, this can't flush frames straight
    // to disk as they're encoded: the ASF header needs the final packet count/size before any bytes
    // are written, and every data packet must be padded to one shared fixed size (chosen from the
    // largest payload actually produced). So encoding happens incrementally, per frame, exactly as
    // PCM arrives -- only the packaging into the ASF container is deferred to Finish(). The buffered
    // data between calls is compressed WMA payloads (kilobytes per second of audio, not the raw PCM
    // an earlier version of AacEncoderSession used to buffer), so this doesn't hold unbounded memory
    // the way that did.
    public sealed class WmaEncoderSession : IAudioSink
    {
        private readonly string _destFilePath;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly WmaFrameEncoder _frameEncoder;
        private readonly List<byte[]> _packetPayloads = [];
        private readonly int[][] _pendingSamples;

        private int _pendingCount;
        private long _totalSamplesWritten;
        private bool _finished;

        private WmaEncoderSession(string destFilePath, int channels, int sampleRate)
        {
            _destFilePath = destFilePath;
            _channels = channels;
            _sampleRate = sampleRate;
            _frameEncoder = new WmaFrameEncoder(channels, sampleRate);

            _pendingSamples = new int[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                _pendingSamples[channel] = new int[_frameEncoder.FrameLength];
            }
        }

        public static WmaEncoderSession OpenSession(string destFilePath, int channels, int sampleRate)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException("Only mono and stereo WMA encoding is supported");
            }

            if (sampleRate <= 0)
            {
                throw new NotSupportedException($"Sample rate {sampleRate} is not a valid WMA sample rate");
            }

            return new WmaEncoderSession(destFilePath, channels, sampleRate);
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var channel = 0; channel < _channels; channel++)
                {
                    _pendingSamples[channel][_pendingCount] = buffer[(frame * _channels) + channel];
                }

                _pendingCount++;
                _totalSamplesWritten++;

                if (_pendingCount == _frameEncoder.FrameLength)
                {
                    EncodePendingBlock();
                }
            }
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            if (_pendingCount > 0)
            {
                for (var channel = 0; channel < _channels; channel++)
                {
                    Array.Clear(_pendingSamples[channel], _pendingCount, _pendingSamples[channel].Length - _pendingCount);
                }

                EncodePendingBlock();
            }

            AsfContainerWriter.Write(_destFilePath, _channels, _sampleRate, _totalSamplesWritten, _packetPayloads);
        }

        public void Dispose()
        {
            // Nothing held open between calls -- AsfContainerWriter owns its own file handle for
            // the single write that happens in Finish().
        }

        private void EncodePendingBlock()
        {
            var block = new double[_channels][];
            for (var channel = 0; channel < _channels; channel++)
            {
                block[channel] = new double[_frameEncoder.FrameLength];
                for (var i = 0; i < _frameEncoder.FrameLength; i++)
                {
                    block[channel][i] = _pendingSamples[channel][i];
                }
            }

            _packetPayloads.Add(_frameEncoder.EncodeFrame(block));
            _pendingCount = 0;
        }
    }
}
