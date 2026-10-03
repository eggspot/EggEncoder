using OggVorbisEncoder;

namespace EggEncoder.Codecs.Vorbis
{
    // Encodes 1 or 2 channels of 16-bit PCM into an Ogg Vorbis file via OggVorbisEncoder (a pure
    // managed C# Vorbis encoder, NuGet package, MIT license -- the same "lean on an established
    // pure-managed implementation" precedent Concentus already sets for this project's own Opus
    // encode/decode). OggVorbisEncoder writes the Ogg container itself (OggStream/ProcessingState/
    // OggPage); unlike Opus, EggEncoder's own from-scratch Codecs/Opus/OggPageWriter is not reused
    // here -- OggVorbisEncoder's own container handling is a complete, independent implementation
    // with its own packet/page object model, not a bare codec needing an external Ogg writer the
    // way Concentus needed one written for Opus.
    //
    // Unlike Opus, Vorbis has no fixed valid sample rate set and no fixed frame size -- any
    // positive sample rate is accepted, and samples are fed to the encoder in whatever chunk sizes
    // arrive via WriteInterleavedSamples, with no need to buffer up to an exact frame boundary
    // first (the encoder's own ProcessingState handles block-size framing internally).
    public sealed class VorbisEncoderSession : IAudioSink
    {
        public const float DefaultQuality = 0.5f;
        private const int SerialNumber = 1;

        private readonly FileStream _destStream;
        private readonly OggStream _oggStream;
        private readonly ProcessingState _processingState;
        private readonly int _channels;
        private readonly float[][] _channelBuffers;

        private bool _disposed;

        private VorbisEncoderSession(FileStream destStream, int channels, int sampleRate, float quality)
        {
            _destStream = destStream;
            _channels = channels;
            // ProcessingState.WriteData expects the outer array's length to exactly equal the
            // channel count (confirmed from OggVorbisEncoder's own example code) -- not a
            // fixed-size-2 array with an unused padding slot for mono.
            _channelBuffers = new float[channels][];

            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            _oggStream = new OggStream(SerialNumber);
            _processingState = ProcessingState.Create(info);

            var comments = new Comments();
            var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
            var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
            var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

            _oggStream.PacketIn(infoPacket);
            _oggStream.PacketIn(commentsPacket);
            _oggStream.PacketIn(booksPacket);

            // Per the Ogg Vorbis spec, the three header packets must be flushed onto their own
            // page(s) before any audio data -- force=true guarantees that even if they'd otherwise
            // fit in a single, still-accumulating page with what comes next.
            FlushPages(force: true);
        }

        public static VorbisEncoderSession OpenSession(string destFilePath, int channels, int sampleRate, int bitsPerSample, float quality = DefaultQuality)
        {
            if (channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {channels} channels; only mono and stereo Vorbis encoding is supported");
            }

            if (bitsPerSample != 16)
            {
                throw new NotSupportedException($"'{destFilePath}' requests {bitsPerSample}-bit samples; only 16-bit Vorbis encoding is supported");
            }

            if (sampleRate <= 0)
            {
                throw new NotSupportedException($"'{destFilePath}' requests a sample rate of {sampleRate}; only positive sample rates are supported for Vorbis encoding");
            }

            var destStream = File.Create(destFilePath);
            try
            {
                return new VorbisEncoderSession(destStream, channels, sampleRate, quality);
            }
            catch
            {
                destStream.Dispose();
                throw;
            }
        }

        public void WriteInterleavedSamples(int[] buffer, int frameCount)
        {
            if (frameCount == 0)
            {
                return;
            }

            for (var c = 0; c < _channels; c++)
            {
                if (_channelBuffers[c] is null || _channelBuffers[c].Length < frameCount)
                {
                    _channelBuffers[c] = new float[frameCount];
                }
            }

            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var c = 0; c < _channels; c++)
                {
                    _channelBuffers[c][frame] = buffer[(frame * _channels) + c] / (float)short.MaxValue;
                }
            }

            _processingState.WriteData(_channelBuffers, frameCount, 0);
            DrainPackets();
        }

        public void Finish()
        {
            _processingState.WriteEndOfStream();
            DrainPackets();
            FlushPages(force: true);
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

        private void DrainPackets()
        {
            while (!_oggStream.Finished && _processingState.PacketOut(out var packet))
            {
                _oggStream.PacketIn(packet);
                FlushPages(force: false);
            }
        }

        private void FlushPages(bool force)
        {
            while (_oggStream.PageOut(out var page, force))
            {
                _destStream.Write(page.Header, 0, page.Header.Length);
                _destStream.Write(page.Body, 0, page.Body.Length);
            }
        }
    }
}
