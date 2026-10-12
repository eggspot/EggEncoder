using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Wav;
using EggEncoder.Results;
using EggEncoder.Waveform;

namespace EggEncoder
{
    // Stream-based Probe overload (docs/feature-roadmap.md item 1) -- a first slice covering the two
    // formats whose own Reader/Decoder classes needed the least adaptation to support a Stream source
    // (WavReader, already FileStream-based internally, just needed that field's type widened to the
    // base Stream and an ownership flag added so Dispose() doesn't close a stream it doesn't own; and
    // FlacDecoder, which already reads its entire source into memory up front via File.ReadAllBytes,
    // so a Stream source just needs reading fully into the same byte[] instead). Every other
    // AudioFormat still throws NotSupportedException here -- adding them is mechanical but each one
    // needs its own careful review (container/codec-specific seekability requirements, and for
    // Opus/Vorbis/MP3, checking whether the underlying Concentus/NVorbis/NLayer dependency's own
    // Stream support imposes any constraint of its own) -- see the roadmap item's own status note.
    public partial class NativeEncoder
    {
        public Task<ProbeResult> Probe(Stream source, AudioFormat format)
        {
            ArgumentNullException.ThrowIfNull(source);

            try
            {
                LogInformation($"Start native stream probe (format {format})");

                var result = format switch
                {
                    AudioFormat.Wav => ProbeWavStream(source),
                    AudioFormat.Flac => ProbeFlacStream(source),
                    _ => throw new NotSupportedException($"Probing an AudioFormat.{format} stream is not supported yet -- see docs/feature-roadmap.md item 1 for status. Use the path-based Probe(string) overload for this format in the meantime.")
                };

                LogInformation($"Completed native stream probe (format {format}): codec '{result.CodecName}', duration {result.DurationSeconds}s");

                return Task.FromResult(result);
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to probe a stream (format {format}) {e.Message}");
                throw;
            }
        }

        // A non-seekable source (e.g. a live HTTP response body) genuinely doesn't have a byte count
        // to report up front -- 0 is this type's own established "unknown" convention for a numeric
        // ProbeResult field (see DurationSeconds's own `sampleRate > 0 ? ... : 0` fallback above),
        // rather than throwing and failing the whole probe over one field a non-seekable source simply
        // cannot provide.
        private static long GetStreamLength(Stream stream) => stream.CanSeek ? stream.Length : 0;

        private static ProbeResult ProbeWavStream(Stream source)
        {
            using var wavReader = WavReader.Open(source);
            var waveformCalculator = new WaveformCalculator(wavReader.TotalSamples, wavReader.Channels, wavReader.BitsPerSample);

            var buffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels));
            }

            var durationSeconds = wavReader.SampleRate > 0 ? (double)wavReader.TotalSamples / wavReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeWavCodec(wavReader.BitsPerSample, wavReader.IsFloatFormat, wavReader.IsImaAdpcm, wavReader.IsALaw, wavReader.IsMuLaw, wavReader.IsMsAdpcm, wavReader.IsYamahaAdpcm);

            return new ProbeResult
            {
                FormatName = "wav",
                FormatLongName = "WAV / WAVE (Waveform Audio)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = codecName,
                CodecLongName = codecLongName,
                SampleRate = wavReader.SampleRate,
                Channels = wavReader.Channels,
                ChannelLayout = DescribeChannelLayout(wavReader.Channels),
                BitsPerSample = wavReader.BitsPerSample,
                BitRate = wavReader.SampleRate * wavReader.BitsPerSample * wavReader.Channels,
                DurationInSamples = wavReader.TotalSamples,
                TimeBase = wavReader.SampleRate > 0 ? $"1/{wavReader.SampleRate}" : null,
                Waveform = waveformCalculator.GetNormalizedWindows(),
                PeakAmplitude = waveformCalculator.GetNormalizedPeakAmplitude(),
                RmsLevel = waveformCalculator.GetNormalizedRmsLevel(),
                Tags = wavReader.Tags
            };
        }

        private static ProbeResult ProbeFlacStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = FlacDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "flac",
                FormatLongName = "FLAC (Free Lossless Audio Codec)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "flac",
                CodecLongName = "FLAC (Free Lossless Audio Codec)",
                SampleRate = streamInfo.SampleRate,
                Channels = streamInfo.Channels,
                ChannelLayout = DescribeChannelLayout(streamInfo.Channels),
                BitsPerSample = streamInfo.BitsPerSample,
                BitRate = streamInfo.SampleRate * streamInfo.BitsPerSample * streamInfo.Channels,
                DurationInSamples = streamInfo.TotalSamples,
                TimeBase = streamInfo.SampleRate > 0 ? $"1/{streamInfo.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? [],
                PeakAmplitude = waveformCalculator?.GetNormalizedPeakAmplitude(),
                RmsLevel = waveformCalculator?.GetNormalizedRmsLevel(),
                Tags = streamInfo.Tags
            };
        }
    }
}
