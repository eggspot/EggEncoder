namespace EggEncoder.Codecs.Alac
{
    // Encodes mono, 16-bit PCM into a CAF/ALAC file -- see AlacDecoder's doc comment for why this tick
    // is scoped to mono/16-bit only. Samples are buffered per ALAC frame (nominally 4096 samples; the
    // final frame may be shorter) and encoded into packets as each frame fills; the packets themselves
    // (compressed, far smaller than the raw PCM Mix already buffers in full) are held in memory until
    // Finish(), since CafWriter's 'pakt' chunk needs every packet's byte size up front.
    public sealed class AlacEncoderSession : IAudioSink
    {
        private readonly FileStream _destStream;
        private readonly AlacSpecificConfig _config;
        private readonly List<byte[]> _packets = [];
        private readonly int[] _pendingSamples;

        private int _pendingCount;
        private long _totalFrames;
        private bool _disposed;

        private AlacEncoderSession(FileStream destStream, AlacSpecificConfig config)
        {
            _destStream = destStream;
            _config = config;
            _pendingSamples = new int[config.FrameLength];
        }

        public static AlacEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample)
        {
            if (channels != 1)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono ALAC encoding is supported");
            }

            if (bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit ALAC encoding is supported");
            }

            var config = new AlacSpecificConfig
            {
                FrameLength = 4096,
                BitDepth = 16,
                Pb = 40,
                Mb = 10,
                Kb = 14,
                NumChannels = 1,
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
                _pendingSamples[_pendingCount++] = buffer[i];

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
            _packets.Add(AlacFrameEncoder.EncodePacket(_pendingSamples, _pendingCount, _config));
            _pendingCount = 0;
        }
    }
}
