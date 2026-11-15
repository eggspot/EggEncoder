using EggEncoder.Codecs;

namespace EggEncoder.Codecs.Mp3
{
    /// <summary>
    /// Decodes MPEG-1, MPEG-2 and MPEG-2.5 Layer III audio (any bit rate, CBR or VBR, mono, stereo,
    /// joint stereo, short and mixed blocks) into interleaved 16-bit PCM blocks, in pure managed code.
    /// Every audio frame is decoded; the encoder delay and padding are not trimmed. MPEG Layer I and
    /// II are not decoded.
    /// </summary>
    public static class Mp3Decoder
    {
        /// <summary>Decodes the MP3 file at <paramref name="mp3FilePath"/> on up to four threads.</summary>
        /// <exception cref="InvalidDataException">The file holds no MPEG audio frames.</exception>
        /// <exception cref="NotSupportedException">The file holds MPEG Layer I or II audio.</exception>
        public static Mp3StreamInfo Decode(string mp3FilePath, AudioBlockDecodedCallback onBlockDecoded) =>
            Decode(mp3FilePath, onBlockDecoded, 0);

        /// <summary>
        /// Decodes the MP3 file at <paramref name="mp3FilePath"/>. Longer files are cut into segments
        /// decoded in parallel; the output is the same for every thread count.
        /// </summary>
        /// <param name="mp3FilePath">The file.</param>
        /// <param name="onBlockDecoded">Receives the samples in order, from the calling thread.</param>
        /// <param name="maxDegreeOfParallelism">Threads to use; 0 or less for up to four.</param>
        /// <exception cref="InvalidDataException">The file holds no MPEG audio frames.</exception>
        /// <exception cref="NotSupportedException">The file holds MPEG Layer I or II audio.</exception>
        public static Mp3StreamInfo Decode(string mp3FilePath, AudioBlockDecodedCallback onBlockDecoded, int maxDegreeOfParallelism)
        {
            ArgumentNullException.ThrowIfNull(mp3FilePath);
            return Decode(File.ReadAllBytes(mp3FilePath), onBlockDecoded, maxDegreeOfParallelism);
        }

        /// <summary>
        /// Decodes an MP3 read from <paramref name="stream"/>'s current position to its end, on up
        /// to four threads. The stream needs no seeking and is not disposed.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream holds no MPEG audio frames.</exception>
        /// <exception cref="NotSupportedException">The stream holds MPEG Layer I or II audio.</exception>
        public static Mp3StreamInfo Decode(Stream stream, AudioBlockDecodedCallback onBlockDecoded) =>
            Decode(stream, onBlockDecoded, 0);

        /// <summary>
        /// Decodes an MP3 read from <paramref name="stream"/>'s current position to its end. The
        /// stream needs no seeking and is not disposed.
        /// </summary>
        /// <param name="stream">The source.</param>
        /// <param name="onBlockDecoded">Receives the samples in order, from the calling thread.</param>
        /// <param name="maxDegreeOfParallelism">Threads to use; 0 or less for up to four.</param>
        /// <exception cref="InvalidDataException">The stream holds no MPEG audio frames.</exception>
        /// <exception cref="NotSupportedException">The stream holds MPEG Layer I or II audio.</exception>
        public static Mp3StreamInfo Decode(Stream stream, AudioBlockDecodedCallback onBlockDecoded, int maxDegreeOfParallelism)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return Decode(memoryStream.ToArray(), onBlockDecoded, maxDegreeOfParallelism);
        }

        internal static Mp3StreamInfo Decode(byte[] fileBytes, AudioBlockDecodedCallback onBlockDecoded, int maxDegreeOfParallelism = 0)
        {
            ArgumentNullException.ThrowIfNull(onBlockDecoded);
            return Mp3StreamDecoder.Decode(fileBytes, onBlockDecoded, maxDegreeOfParallelism);
        }
    }

    /// <summary>The format of a decoded MP3.</summary>
    public class Mp3StreamInfo
    {
        /// <summary>Channels in the decoded blocks: 1 or 2.</summary>
        public required int Channels { get; init; }

        /// <summary>Samples per second.</summary>
        public required int SampleRate { get; init; }

        /// <summary>Always 16.</summary>
        public required int BitsPerSample { get; init; }

        /// <summary>Samples per channel: the audio frames times the samples per frame.</summary>
        public required long TotalSamples { get; init; }
    }
}
