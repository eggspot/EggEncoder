using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Au;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Codecs.Opus;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Vorbis;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.Codecs.Wma;
using EggEncoder.Results;
using EggEncoder.Waveform;

namespace EggEncoder
{
    // Stream-based Probe overload (docs/feature-roadmap.md item 1) -- covers every format whose own
    // Reader/Decoder class has been adapted for a Stream source so far: WAV/AIFF/AU/CAF (FileStream-
    // field-widened-to-Stream, each requiring CanSeek since their own header parse relies on
    // Stream.Position/Seek throughout), FLAC/AAC/WavPack/Opus/WMA (read-fully-into-byte[], no seek
    // needed), TTA (read-strictly-sequentially, also no seek needed), and Vorbis (requires CanSeek --
    // confirmed directly, by feeding NVorbis's own VorbisReader a non-seekable stream and observing
    // it throw; unlike every other codec here, NVorbis's own constructor already has first-class
    // non-disposing support via closeOnDispose: false, so no "widen"/"read fully into memory"
    // adaptation was needed for Vorbis at all), and MOV/MP4 (the same CanSeek-requiring
    // "FileStream-widened-to-Stream" treatment WAV/AIFF/AU/CAF already got, via MovProbe.Probe(Stream)/
    // MovDecoder.Decode(Stream, ...)). MP3 still throws NotSupportedException here -- NLayer's own
    // MpegFile(Stream) constructor has no leaveOpen option, so it can't honor this type's own "never
    // dispose the caller's stream" contract without further work -- see the roadmap item's own status
    // note.
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
                    AudioFormat.Aiff => ProbeAiffStream(source),
                    AudioFormat.Au => ProbeAuStream(source),
                    AudioFormat.Flac => ProbeFlacStream(source),
                    AudioFormat.Aac => ProbeAacStream(source),
                    AudioFormat.WavPack => ProbeWavPackStream(source),
                    AudioFormat.Tta => ProbeTtaStream(source),
                    AudioFormat.Caf => ProbeAlacStream(source),
                    AudioFormat.Opus => ProbeOpusStream(source),
                    AudioFormat.Vorbis => ProbeVorbisStream(source),
                    AudioFormat.Wma => ProbeWmaStream(source),
                    AudioFormat.Mov => ProbeMovStream(source),
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

        /// <summary>
        /// Checks <paramref name="cancellationToken"/> once, up front, before any decode work starts
        /// -- see <see cref="IMediaEncoder.Probe(string, CancellationToken)"/>'s own doc comment for
        /// why this scope boundary was chosen (docs/feature-roadmap.md item 5).
        /// </summary>
        public Task<ProbeResult> Probe(Stream source, AudioFormat format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Probe(source, format);
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
            var waveformCalculator = new WaveformCalculator(wavReader.TotalSamples, wavReader.Channels, wavReader.BitsPerSample, sampleRate: wavReader.SampleRate);

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
                IntegratedLoudness = waveformCalculator.GetIntegratedLoudness(),
                Tags = wavReader.Tags
            };
        }

        private static ProbeResult ProbeFlacStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = FlacDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness(),
                Tags = streamInfo.Tags
            };
        }

        private static ProbeResult ProbeAiffStream(Stream source)
        {
            using var aiffReader = AiffReader.Open(source);
            var waveformCalculator = new WaveformCalculator(aiffReader.TotalSamples, aiffReader.Channels, aiffReader.BitsPerSample, sampleRate: aiffReader.SampleRate);

            var buffer = new int[FramesPerBlock * aiffReader.Channels];

            int framesRead;
            while ((framesRead = aiffReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * aiffReader.Channels));
            }

            var durationSeconds = aiffReader.SampleRate > 0 ? (double)aiffReader.TotalSamples / aiffReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeAiffCodec(aiffReader.BitsPerSample, aiffReader.IsLittleEndian, aiffReader.IsFloat32, aiffReader.IsFloat64, aiffReader.IsALaw, aiffReader.IsMuLaw, aiffReader.IsIma4);

            return new ProbeResult
            {
                FormatName = "aiff",
                FormatLongName = "AIFF (Audio Interchange File Format)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = codecName,
                CodecLongName = codecLongName,
                SampleRate = aiffReader.SampleRate,
                Channels = aiffReader.Channels,
                ChannelLayout = DescribeChannelLayout(aiffReader.Channels),
                BitsPerSample = aiffReader.BitsPerSample,
                BitRate = aiffReader.SampleRate * aiffReader.BitsPerSample * aiffReader.Channels,
                DurationInSamples = aiffReader.TotalSamples,
                TimeBase = aiffReader.SampleRate > 0 ? $"1/{aiffReader.SampleRate}" : null,
                Waveform = waveformCalculator.GetNormalizedWindows(),
                PeakAmplitude = waveformCalculator.GetNormalizedPeakAmplitude(),
                RmsLevel = waveformCalculator.GetNormalizedRmsLevel(),
                IntegratedLoudness = waveformCalculator.GetIntegratedLoudness(),
                Tags = aiffReader.Tags
            };
        }

        private static ProbeResult ProbeAuStream(Stream source)
        {
            using var auReader = AuReader.Open(source);
            var waveformCalculator = new WaveformCalculator(auReader.TotalSamples, auReader.Channels, auReader.BitsPerSample, sampleRate: auReader.SampleRate);

            var buffer = new int[FramesPerBlock * auReader.Channels];

            int framesRead;
            while ((framesRead = auReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * auReader.Channels));
            }

            var durationSeconds = auReader.SampleRate > 0 ? (double)auReader.TotalSamples / auReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeAuCodec(auReader.BitsPerSample, auReader.IsFloat32, auReader.IsFloat64, auReader.IsALaw, auReader.IsMuLaw);

            return new ProbeResult
            {
                FormatName = "au",
                FormatLongName = "Sun AU",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = codecName,
                CodecLongName = codecLongName,
                SampleRate = auReader.SampleRate,
                Channels = auReader.Channels,
                ChannelLayout = DescribeChannelLayout(auReader.Channels),
                BitsPerSample = auReader.BitsPerSample,
                BitRate = auReader.SampleRate * auReader.BitsPerSample * auReader.Channels,
                DurationInSamples = auReader.TotalSamples,
                TimeBase = auReader.SampleRate > 0 ? $"1/{auReader.SampleRate}" : null,
                Waveform = waveformCalculator.GetNormalizedWindows(),
                PeakAmplitude = waveformCalculator.GetNormalizedPeakAmplitude(),
                RmsLevel = waveformCalculator.GetNormalizedRmsLevel(),
                IntegratedLoudness = waveformCalculator.GetIntegratedLoudness()
            };
        }

        private static ProbeResult ProbeAacStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = AacDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "aac",
                FormatLongName = "ADTS AAC (Advanced Audio Coding)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "aac",
                CodecLongName = "AAC-LC (Advanced Audio Coding, Low Complexity profile)",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness()
            };
        }

        private static ProbeResult ProbeWavPackStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = WavPackDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "wv",
                FormatLongName = "WavPack",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "wavpack",
                CodecLongName = "WavPack",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness()
            };
        }

        private static ProbeResult ProbeTtaStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = TtaDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "tta",
                FormatLongName = "TTA (True Audio)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "tta",
                CodecLongName = "TTA (True Audio) lossless",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness()
            };
        }

        private static ProbeResult ProbeAlacStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = AlacDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "caf",
                FormatLongName = "CAF (Core Audio Format)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "alac",
                CodecLongName = "ALAC (Apple Lossless Audio Codec)",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness()
            };
        }

        private static ProbeResult ProbeOpusStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = OpusDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "ogg",
                FormatLongName = "Ogg",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "opus",
                CodecLongName = "Opus (Opus Interactive Audio Codec)",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness(),
                Tags = streamInfo.Tags
            };
        }

        private static ProbeResult ProbeVorbisStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = VorbisDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "ogg",
                FormatLongName = "Ogg",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "vorbis",
                CodecLongName = "Vorbis",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness(),
                Tags = streamInfo.Tags
            };
        }

        private static ProbeResult ProbeWmaStream(Stream source)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = WmaDecoder.Decode(source, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample, sampleRate: sampleRate);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "asf",
                FormatLongName = "ASF (Advanced / Active Streaming Format)",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = durationSeconds,
                CodecType = "audio",
                CodecName = "wmav2",
                CodecLongName = "Windows Media Audio 2",
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
                IntegratedLoudness = waveformCalculator?.GetIntegratedLoudness()
            };
        }

        // The Stream-based counterpart to NativeEncoder.cs's own path-based ProbeVideo -- same
        // MovProbe/MovDecoder composition (video metadata plus, if present, a decodable mono AAC-LC
        // audio track's own waveform/level data), via TryDecodeAudioTrackCore's shared delegate shape
        // so that "decode, degrade to null on any failure, map to DecodedAudioTrack" logic isn't
        // duplicated here. Unlike the path-based overload, there is no file extension to distinguish
        // a MOV container from an MP4 one -- AudioFormat.Mov covers both -- so this always reports
        // "mov"/"QuickTime / MOV", the same default the path-based overload itself falls back to for
        // any extension other than ".mp4".
        private static ProbeResult ProbeMovStream(Stream source)
        {
            var movProbeResult = MovProbe.Probe(source);
            var audio = TryDecodeAudioTrackCore(onBlockDecoded => MovDecoder.Decode(source, onBlockDecoded));

            return new ProbeResult
            {
                FormatName = "mov",
                FormatLongName = "QuickTime / MOV",
                SizeBytes = GetStreamLength(source),
                DurationSeconds = movProbeResult.DurationSeconds,
                CodecType = "video",
                CodecName = movProbeResult.CodecFourCc,
                Width = movProbeResult.Width,
                Height = movProbeResult.Height,
                SampleRate = audio?.SampleRate,
                Channels = audio?.Channels,
                ChannelLayout = audio is null ? null : DescribeChannelLayout(audio.Channels),
                BitsPerSample = audio?.BitsPerSample,
                BitRate = audio is null ? null : audio.SampleRate * audio.BitsPerSample * audio.Channels,
                DurationInSamples = audio?.TotalSamples,
                TimeBase = audio is not null && audio.SampleRate > 0 ? $"1/{audio.SampleRate}" : null,
                Waveform = audio?.Waveform,
                PeakAmplitude = audio?.PeakAmplitude,
                RmsLevel = audio?.RmsLevel,
                IntegratedLoudness = audio?.IntegratedLoudness
            };
        }
    }
}
