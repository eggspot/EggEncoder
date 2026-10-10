using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;
using System.Runtime.InteropServices;

namespace EggEncoder.Codecs.Mp3
{
    // Pure managed MPEG-1 Layer III encoder, clean-room from the published ISO/IEC 11172-3 standard
    // text (see Mp3Tables/Mp3HuffmanTables/Mp3PolyphaseFilter/Mp3AliasReduction/Mp3Quantizer/
    // Mp3FrameEncoder's own doc comments for the per-stage provenance) -- no LAME, GroovyCodecs, or
    // any other encoder's source was consulted, per docs/managed-codec-rewrite-plan.md item 6.
    //
    // A deliberately simple CBR-only baseline: long blocks only (no block switching/transient
    // handling), independent (non-joint) stereo, no psychoacoustic model (global_gain alone is
    // searched to fit each granule's own fixed bit budget), and no bit-reservoir borrowing across
    // frames -- matching this project's own "correctness first, not yet compression-competitive"
    // precedent (see FlacEncoder's own items 2/3 history, and the WavPack encoder's own
    // single-decorrelation-term MVP choice). Correctness is the bar: every frame this encoder
    // writes decodes cleanly via this project's own Mp3Decoder (NLayer).
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
            var destStream = File.Create(destMp3FilePath);
            return new Mp3EncoderSession(destStream, channels, sampleRate, bitsPerSample, bitRateKbps);
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

        private bool _finished;
        private bool _disposed;

        internal Mp3EncoderSession(FileStream destStream, int channels, int sampleRate, int bitsPerSample, int bitRateKbps)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSampleShift = bitsPerSample - 16;
            _frameEncoder = new Mp3FrameEncoder(channels, sampleRate, bitRateKbps);
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
                _pendingInterleaved.Add(_bitsPerSampleShift > 0 ? sample >> _bitsPerSampleShift : sample);
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

            if (_pendingInterleaved.Count > 0)
            {
                // The encoder's own polyphase/hybrid filterbank needs exactly 1152 samples per
                // channel per frame -- silence-pad the final, shorter-than-a-frame tail rather than
                // special-casing a partial frame. A few dozen milliseconds of trailing silence is
                // the normal cost of any block-based encoder's own fixed frame size, not specific to
                // this implementation.
                var remainingFrames = _pendingInterleaved.Count / _channels;
                var paddingFrames = FrameSamples - remainingFrames;
                for (var i = 0; i < paddingFrames * _channels; i++)
                {
                    _pendingInterleaved.Add(0);
                }

                EncodeFrame();
            }
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
        }
    }
}
