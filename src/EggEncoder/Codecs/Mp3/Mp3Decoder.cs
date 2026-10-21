using EggEncoder.Codecs;
using NLayer;

namespace EggEncoder.Codecs.Mp3
{
    public static class Mp3Decoder
    {
        private const int BytesPerSample = 2;
        private const int BitsPerSample = 16;
        private const int FramesPerBlock = 4096;

        public static Mp3StreamInfo Decode(string mp3FilePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var mpegFile = new MpegFile(mp3FilePath);
            return Decode(mpegFile, onBlockDecoded);
        }

        /// <summary>
        /// Decodes an MP3 source from an already-open <see cref="Stream"/> instead of a file path
        /// -- for a source that isn't a local file (an HTTP response body, blob storage, an
        /// in-memory buffer). No <see cref="Stream.CanSeek"/> requirement: like
        /// <see cref="Flac.FlacDecoder"/>/<see cref="Aac.AacDecoder"/>, this reads the whole source
        /// (from its current position) into memory up front, so any forward-readable stream works.
        /// That buffer is also what lets this honor the "never dispose the caller's stream"
        /// contract every Stream overload here shares: NLayer's own <c>MpegFile(Stream)</c>
        /// constructor has no <c>leaveOpen</c> option and always disposes the stream it's given,
        /// so it only ever sees this method's own <see cref="MemoryStream"/>, never
        /// <paramref name="stream"/> itself. Does NOT take ownership of <paramref name="stream"/>.
        /// </summary>
        public static Mp3StreamInfo Decode(Stream stream, AudioBlockDecodedCallback onBlockDecoded)
        {
            ArgumentNullException.ThrowIfNull(stream);

            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);

            return Decode(memoryStream.ToArray(), onBlockDecoded);
        }

        // internal (not private) so NativeEncoder.Probe(Stream, AudioFormat.Mp3) can read the
        // caller's (possibly non-seekable) stream exactly once, then run both Mp3Probe.Probe and
        // this decode over separate read-only MemoryStream views of the same bytes -- NLayer
        // disposing its own view never affects the other one.
        internal static Mp3StreamInfo Decode(byte[] fileBytes, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var mpegFile = new MpegFile(new MemoryStream(fileBytes, writable: false));
            return Decode(mpegFile, onBlockDecoded);
        }

        private static Mp3StreamInfo Decode(MpegFile mpegFile, AudioBlockDecodedCallback onBlockDecoded)
        {
            var channels = mpegFile.Channels;
            var sampleRate = mpegFile.SampleRate;
            var totalSamplesPerChannel = (long)(mpegFile.Duration.TotalSeconds * sampleRate);

            var byteBuffer = new byte[FramesPerBlock * channels * BytesPerSample];
            var interleavedBuffer = new int[FramesPerBlock * channels];

            int bytesRead;
            while ((bytesRead = mpegFile.ReadSamplesInt16(byteBuffer, 0, byteBuffer.Length)) > 0)
            {
                var sampleCount = bytesRead / BytesPerSample;
                for (var i = 0; i < sampleCount; i++)
                {
                    interleavedBuffer[i] = (short)(byteBuffer[i * 2] | (byteBuffer[(i * 2) + 1] << 8));
                }

                onBlockDecoded(new ReadOnlySpan<int>(interleavedBuffer, 0, sampleCount), channels, sampleRate, BitsPerSample, totalSamplesPerChannel);
            }

            return new Mp3StreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = BitsPerSample,
                TotalSamples = totalSamplesPerChannel
            };
        }
    }

    public class Mp3StreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
