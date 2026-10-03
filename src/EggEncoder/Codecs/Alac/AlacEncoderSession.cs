namespace EggEncoder.Codecs.Alac
{
    // Encodes 1 or 2 channels of 16-bit or 24-bit PCM into a CAF/ALAC file -- see AlacDecoder's doc
    // comment for why 20-bit is excluded and why always writing extraBitsBytes=0 is spec-valid, not
    // just a self-referential simplification. Samples are de-interleaved and buffered per channel, per ALAC
    // frame (nominally 4096 frames; the final frame may be shorter), and encoded into packets as each
    // frame fills; the packets themselves (compressed, far smaller than the raw PCM Mix already
    // buffers in full) are held in memory until Finish(), since CafWriter's 'pakt' chunk needs every
    // packet's byte size up front.
    public sealed class AlacEncoderSession : IAudioSink
    {
        private readonly FileStream _destStream;
        private readonly AlacSpecificConfig _config;
        private readonly int _channels;
        private readonly List<byte[]> _packets = [];
        private readonly int[][] _pendingChannelSamples;

        private int _pendingCount;
        private long _totalFrames;
        private bool _disposed;

        private AlacEncoderSession(FileStream destStream, AlacSpecificConfig config)
        {
            _destStream = destStream;
            _config = config;
            _channels = config.NumChannels;

            _pendingChannelSamples = new int[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _pendingChannelSamples[c] = new int[config.FrameLength];
            }
        }

        public static AlacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo ALAC encoding is supported");
            }

            if (bitsPerSample is not 16 and not 24)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit and 24-bit ALAC encoding is supported");
            }

            var config = new AlacSpecificConfig
            {
                FrameLength = 4096,
                BitDepth = bitsPerSample,
                Pb = 40,
                Mb = 10,
                Kb = 14,
                NumChannels = channels,
                MaxRun = 255,
                SampleRate = sampleRate
            };

            var destStream = File.Create(destFilePath);
            try
            {
                return new AlacEncoderSession(destStream, config);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            for (var i = 0; i < frameCount; i++)
            {
                for (var c = 0; c < _channels; c++)
                {
                    _pendingChannelSamples[c][_pendingCount] = buffer[(i * _channels) + c];
                }

                _pendingCount++;
                if (_pendingCount == _config.FrameLength)
                {
                    FlushPendingFrame();
                }
            }

            _totalFrames += frameCount;
        }

        public void Finish()
        {
            if (_pendingCount > 0)
            {
                FlushPendingFrame();
            }

            CafWriter.Write(_destStream, _config, _packets, _totalFrames);
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
            // _pendingChannelSamples may be longer than _pendingCount for a short final frame; every
            // downstream step (AlacLpcPredictor, AlacRiceCoder, the verbatim fallback) is bounded by
            // the sampleCount argument, never the array length, so passing the live buffers directly
            // is safe -- EncodePacket fully consumes them synchronously before this returns.
            _packets.Add(AlacFrameEncoder.EncodePacket(_pendingChannelSamples, _pendingCount, _config));
            _pendingCount = 0;
        }
    }
}
