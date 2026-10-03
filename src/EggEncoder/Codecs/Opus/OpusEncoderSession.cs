using Concentus;
using Concentus.Enums;

namespace EggEncoder.Codecs.Opus
{
    // Encodes 1 or 2 channels of 16-bit PCM into an OggOpus file via Concentus (a pure managed
    // Opus implementation -- see OpusRuntimeConfiguration for why this never falls back to a
    // native libopus even if one happens to be on the host). Fixed at 48kHz, Opus's native/highest
    // internal rate: Opus only operates at 8/12/16/24/48kHz internally regardless of what a file's
    // OpusHead claims as the "original" rate, so rather than silently resampling an arbitrary
    // source rate down to one of those five, this requires the caller's source to already be
    // 48kHz -- resample first via the existing ResamplingTransform/PcmTransformPipeline machinery
    // if it isn't, the same way every other bit-depth/rate mismatch in this codebase is handled
    // (validate and reject, don't silently transform).
    //
    // Samples are buffered per 20ms frame (960 samples per channel at 48kHz -- one of Opus's fixed
    // valid frame durations). Unlike every other codec here, Opus frames must be an exact one of
    // its fixed sizes; there is no "shorter final frame" option, so Finish() zero-pads a partial
    // final frame up to 960 samples before encoding it -- the output will contain a few extra
    // silent samples past what was actually written, a real and documented limitation of a lossy,
    // fixed-frame-size codec, not a bug.
    public sealed class OpusEncoderSession : IAudioSink
    {
        private const int SampleRate = 48000;
        private const int FrameLength = 960; // 20ms at 48kHz
        private const uint SerialNumber = 1;

        private readonly FileStream _destStream;
        private readonly OggPageWriter _pageWriter;
        private readonly IOpusEncoder _encoder;
        private readonly int _channels;
        private readonly short[] _pendingSamples;
        private readonly byte[] _outputBuffer = new byte[4000]; // Opus packets are at most 1275 bytes; comfortably oversized

        private int _pendingCount;
        private long _granulePosition;
        private bool _disposed;

        private OpusEncoderSession(FileStream destStream, int channels)
        {
            _destStream = destStream;
            _channels = channels;
            _pageWriter = new OggPageWriter(destStream, SerialNumber);
            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            _pendingSamples = new short[FrameLength * channels];

            _pageWriter.WritePacket(OpusHeaderPackets.BuildOpusHead(channels, _encoder.Lookahead, SampleRate), granulePosition: 0, isEndOfStream: false);
            _pageWriter.WritePacket(OpusHeaderPackets.BuildOpusTags(), granulePosition: 0, isEndOfStream: false);
        }

        public static OpusEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo Opus encoding is supported");
            }

            if (bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit Opus encoding is supported");
            }

            if (sampleRate != SampleRate)
            {
                throw new NotSupportedException($"'{destFilePath}' requests a {sampleRate}Hz source; Opus encoding here is fixed at {SampleRate}Hz -- resample first via ResamplingTransform");
            }

            var destStream = File.Create(destFilePath);
            try
            {
                return new OpusEncoderSession(destStream, channels);
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
                var samplesToCopy = Math.Min(_pendingSamples.Length - _pendingCount, sampleCount - bufferOffset);

                for (var i = 0; i < samplesToCopy; i++)
                {
                    _pendingSamples[_pendingCount + i] = (short)buffer[bufferOffset + i];
                }

                _pendingCount += samplesToCopy;
                bufferOffset += samplesToCopy;

                if (_pendingCount == _pendingSamples.Length)
                {
                    EncodeAndWritePendingFrame(isEndOfStream: false);
                }
            }
        }

        public void Finish()
        {
            // The encoder has a fixed internal lookahead (_encoder.Lookahead, written into
            // OpusHead's pre_skip): without draining it, the true tail of the real input -- up to
            // that many samples -- never leaves the encoder's internal buffer and is silently
            // lost, rather than appearing (delayed) in an encoded frame. Feeding that many extra
            // zero samples through the normal pipeline pushes the genuine tail out; the decoder's
            // own pre_skip trim at the *start* of the stream is what makes this net out correctly,
            // at the cost of up to one frame (960 samples, ~20ms) of trailing silence beyond the
            // real content -- the same quantization every fixed-frame-size Opus encoder accepts.
            // Skipped entirely if nothing was ever written, so an empty session still produces a
            // file with zero audio packets (TotalSamples=0 on decode), matching every other
            // codec's own "Finish with no samples written" behavior.
            if (_pendingCount == 0 && _granulePosition == 0)
            {
                return;
            }

            var lookahead = _encoder.Lookahead;
            if (lookahead > 0)
            {
                WriteInterleavedSamples(new int[lookahead * _channels], lookahead);
            }

            Array.Clear(_pendingSamples, _pendingCount, _pendingSamples.Length - _pendingCount);
            EncodeAndWritePendingFrame(isEndOfStream: true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encoder.Dispose();
            _destStream.Dispose();
        }

        private void EncodeAndWritePendingFrame(bool isEndOfStream)
        {
            var encodedLength = _encoder.Encode(_pendingSamples, FrameLength, _outputBuffer, _outputBuffer.Length);
            _granulePosition += FrameLength;
            _pageWriter.WritePacket(_outputBuffer[..encodedLength], _granulePosition, isEndOfStream);
            _pendingCount = 0;
        }
    }
}
