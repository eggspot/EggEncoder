using NVorbis;

namespace EggEncoder.Codecs.Vorbis
{
    // Decodes an Ogg Vorbis file via NVorbis (a pure managed C# Vorbis decoder, NuGet package, MIT
    // license -- the same "lean on an established pure-managed implementation" precedent NLayer
    // and Concentus already set for this project's own MP3 decode and Opus encode/decode).
    // NVorbis demuxes the Ogg container itself; unlike Opus, EggEncoder's own from-scratch
    // Codecs/Opus/OggPageReader is not involved here at all (NVorbis's VorbisReader is a complete
    // "open this .ogg file and get PCM out" API on its own, not a bare codec needing an external
    // container reader the way Concentus needed one written for Opus).
    //
    // Scoped to mono and stereo, matching every other codec's convention in this project --
    // NVorbis itself supports arbitrary channel counts, but OggVorbisEncoder (the encode side)
    // only documents mono/stereo as fully supported, so decode is scoped to match rather than
    // accepting third-party files this project's own encoder could never produce.
    //
    // NVorbis's own PCM output is normalized float (-1.0..1.0, standard Vorbis convention); unlike
    // Opus (where Concentus offers a direct int16 decode overload), there is no int16 output option
    // here, so this converts explicitly -- mirroring WmaDecoder's own choice to report 16-bit
    // output despite having no fixed native bit depth internally, for consistency across this
    // project's lossy codecs.
    public static class VorbisDecoder
    {
        private const int ReadBufferFrames = 4096;

        public static VorbisStreamInfo Decode(string filePath, AudioBlockDecodedCallback onBlockDecoded)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new VorbisReader(stream, closeOnDispose: false);

            if (reader.Channels is not 1 and not 2)
            {
                throw new NotSupportedException($"'{filePath}' has {reader.Channels} channels; only mono and stereo Vorbis are supported");
            }

            var channels = reader.Channels;
            var sampleRate = reader.SampleRate;
            var totalSamples = reader.TotalSamples;

            var floatBuffer = new float[ReadBufferFrames * channels];

            int floatsRead;
            while ((floatsRead = reader.ReadSamples(floatBuffer)) > 0)
            {
                var block = new int[floatsRead];
                for (var i = 0; i < floatsRead; i++)
                {
                    block[i] = ToSixteenBitRange(floatBuffer[i]);
                }

                onBlockDecoded(block, channels, sampleRate, 16, totalSamples);
            }

            return new VorbisStreamInfo
            {
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = 16,
                TotalSamples = totalSamples
            };
        }

        // Mirrors EggEncoder.Pcm.FloatSampleConverter.ClampToNativeInt32's exact convention (NaN ->
        // silence, out-of-range clamped to +/-1.0 first) but scaled to the 16-bit range this
        // decoder reports, rather than that helper's own fixed 32-bit scale -- intentionally a
        // sibling implementation, not a reuse, since FloatSampleConverter is documented as
        // specifically a 32-bit-range conversion for a different call site (the float PCM
        // application boundary), not a general any-bit-depth helper.
        private static int ToSixteenBitRange(float sample)
        {
            if (float.IsNaN(sample))
            {
                return 0;
            }

            var clamped = Math.Clamp(sample, -1f, 1f);
            return (int)(clamped * short.MaxValue);
        }
    }

    public class VorbisStreamInfo
    {
        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int BitsPerSample { get; init; }

        public required long TotalSamples { get; init; }
    }
}
