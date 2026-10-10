using EggEncoder.Codecs.Wav;

namespace EggEncoder.Codecs.WavPack
{
    // Pure managed WavPack (.wv) encoder -- see WavPackDecoder's own doc comment for the general
    // provenance/scoping rationale (format spec for the container, clean-room-derived algorithm
    // knowledge for the codec itself), which applies equally here: every formula is the direct
    // mathematical inverse of this project's own already bit-exact-verified decoder, not a fresh
    // study of any encoder implementation.
    //
    // An MVP scope, deliberately simple rather than compression-competitive -- see
    // WavPackBlockEncoder's own doc comment for the exact simplifications (a single fixed
    // decorrelation term, independent-channel stereo, no real zero-run-length exploitation).
    // Correctness is the bar: every block this encoder writes decodes back to the exact original
    // samples through this project's own WavPackDecoder, the primary test oracle (see
    // WavPackEncoderSessionTest/WavPackRoundTripTest/WavPackFfmpegCrossCheckTest).
    //
    // Checked, but NOT yet achieved, during development: byte-for-byte compatibility with the real
    // reference wvunpack CLI. It accepts this encoder's output for plenty of content, but rejects
    // some of it outright ("not compatible with this version of WavPack file!") in a way this
    // project's own decoder never reproduces or explains -- confirmed (via the real wavpack CLI's
    // own encoder, at every processing level including its fastest/simplest "-x0") that no real
    // encoder ever actually emits a single-decorrelation-term block the way this one deliberately
    // does for MVP simplicity, which is the leading suspect for why the real decoder sometimes
    // balks at a shape no real encoder has ever handed it. Rather than block this item on fully
    // reverse-engineering that undocumented decoder-side behavior, this is left as a known
    // limitation -- see docs/managed-codec-rewrite-plan.md item 5's own status note.
    public static class WavPackEncoder
    {
        private const int FramesPerBlock = 4096;

        public static void Encode(string sourceWavFilePath, string destWvFilePath)
        {
            using var wavReader = WavReader.Open(sourceWavFilePath);
            using var session = WavPackEncoderSession.OpenSession(destWvFilePath, wavReader.Channels, wavReader.BitsPerSample, wavReader.SampleRate, wavReader.TotalSamples);

            var interleavedBuffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(interleavedBuffer, FramesPerBlock)) > 0)
            {
                session.WriteInterleavedSamples(interleavedBuffer, framesRead);
            }

            session.Finish();
        }
    }

    public sealed class WavPackEncoderSession : IAudioSink
    {
        private const int BlockSamples = 4096;

        private readonly FileStream _destStream;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _sampleRate;
        private readonly long _totalSamples;
        private readonly List<int> _pendingInterleaved = [];

        private long _samplesWritten;
        private bool _finished;
        private bool _disposed;

        private WavPackEncoderSession(FileStream destStream, int channels, int bitsPerSample, int sampleRate, long totalSamples)
        {
            _destStream = destStream;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _sampleRate = sampleRate;
            _totalSamples = totalSamples;
        }

        public static WavPackEncoderSession OpenSession(string destFilePath, int channels, int bitsPerSample, int sampleRate, long totalSamples)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo WavPack encoding is supported");
            }

            if (bitsPerSample != 16 && bitsPerSample != 24)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit and 24-bit WavPack encoding is supported");
            }

            if (sampleRate <= 0)
            {
                throw new NotSupportedException($"'{destFilePath}' requests a sample rate of {sampleRate}; only positive sample rates are supported for WavPack encoding");
            }

            if (totalSamples <= 0)
            {
                // WavPack genuinely cannot represent an empty/zero-sample stream at all -- confirmed
                // from its own reference CLI, which refuses to encode one outright (see
                // WavPackEncoderSessionTest.OpenSession_With_ZeroTotalSamples_Should_Throw's own
                // comment for the original, native-API-specific chain of evidence this contract
                // predates and still matches).
                throw new NotSupportedException($"'{destFilePath}' requests {totalSamples} total samples; WavPack cannot encode an empty/zero-sample stream");
            }

            var destStream = File.Create(destFilePath);
            return new WavPackEncoderSession(destStream, channels, bitsPerSample, sampleRate, totalSamples);
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            for (var i = 0; i < frameCount * _channels; i++)
            {
                _pendingInterleaved.Add(buffer[i]);
            }

            while (_pendingInterleaved.Count >= BlockSamples * _channels)
            {
                FlushBlock(BlockSamples);
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
                FlushBlock(_pendingInterleaved.Count / _channels);
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

        private void FlushBlock(int frameCount)
        {
            var channelSamples = new int[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                channelSamples[c] = new int[frameCount];
            }

            for (var i = 0; i < frameCount; i++)
            {
                for (var c = 0; c < _channels; c++)
                {
                    channelSamples[c][i] = _pendingInterleaved[(i * _channels) + c];
                }
            }

            var blockBytes = WavPackBlockWriter.WriteBlock(channelSamples, _channels, _bitsPerSample, _sampleRate, _samplesWritten, _totalSamples);
            _destStream.Write(blockBytes);

            _samplesWritten += frameCount;
            _pendingInterleaved.RemoveRange(0, frameCount * _channels);
        }
    }
}
