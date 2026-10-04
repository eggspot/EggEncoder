using EggEncoder.Codecs;
using EggEncoder.Codecs.Aac;
using EggEncoder.Codecs.Aiff;
using EggEncoder.Codecs.Alac;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mov;
using EggEncoder.Codecs.Mp3;
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
    public partial class NativeEncoder : IMediaEncoder
    {
        private const int FramesPerBlock = 4096;

        private readonly ILogger _logger;
        private readonly bool _loggingEnabled;

        public NativeEncoder(ILogger<NativeEncoder> logger, bool enableLogging = true)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggingEnabled = enableLogging;
        }

        public Task<ProbeResult> Probe(string filePath)
        {
            try
            {
                LogInformation($"Start native probe '{filePath}'");

                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var result = extension switch
                {
                    ".wav" => ProbeWav(filePath),
                    ".aiff" or ".aif" => ProbeAiff(filePath),
                    ".flac" => ProbeFlac(filePath),
                    ".mp3" => ProbeMp3(filePath),
                    ".aac" => ProbeAac(filePath),
                    ".wma" => ProbeWma(filePath),
                    ".caf" => ProbeAlac(filePath),
                    ".tta" => ProbeTta(filePath),
                    ".opus" => ProbeOpus(filePath),
                    ".ogg" => ProbeVorbis(filePath),
                    ".wv" => ProbeWavPack(filePath),
                    ".mov" or ".mp4" => ProbeVideo(filePath),
                    _ => throw new NotSupportedException($"Probing '{extension}' files is not supported by the native audio encoder")
                };

                LogInformation($"Completed native probe '{filePath}': format '{result.FormatName}', codec '{result.CodecName}', duration {result.DurationSeconds}s");

                return Task.FromResult(result);
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to probe '{filePath}' {e.Message}");
                throw;
            }
        }

        public Task ConvertFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                LogInformation($"Start native convert '{sourceFilePath}' to '{destFilePath}'");

                EnsureDestinationDirectory(destFilePath);
                AudioCutter.Convert(sourceFilePath, destFilePath);

                LogInformation($"Completed native convert '{sourceFilePath}' to '{destFilePath}'");

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to convert from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        public Task CutFile(string sourceFilePath, string destFilePath, int startInSeconds, int endInSeconds)
        {
            try
            {
                LogInformation($"Start native cut '{sourceFilePath}' to '{destFilePath}' start {startInSeconds} end {endInSeconds}");

                EnsureDestinationDirectory(destFilePath);
                var produced = AudioCutter.Cut(sourceFilePath, destFilePath, startInSeconds, endInSeconds);

                LogInformation(produced
                    ? $"Completed native cut '{sourceFilePath}' to '{destFilePath}'"
                    : $"Completed native cut '{sourceFilePath}' to '{destFilePath}': requested range was outside the source duration, no file written");

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                LogError(e, $"Failed to cut from '{sourceFilePath}' to '{destFilePath}' {e.Message}");
                throw;
            }
        }

        private void LogInformation(string message)
        {
            if (_loggingEnabled)
            {
                _logger.LogInformation(message);
            }
        }

        private void LogError(Exception exception, string message)
        {
            if (_loggingEnabled)
            {
                _logger.LogError(exception, message);
            }
        }

        private static void EnsureDestinationDirectory(string destFilePath)
        {
            var directory = Path.GetDirectoryName(destFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }
        }

        private static ProbeResult ProbeWav(string filePath)
        {
            using var wavReader = WavReader.Open(filePath);
            var waveformCalculator = new WaveformCalculator(wavReader.TotalSamples, wavReader.Channels, wavReader.BitsPerSample);

            var buffer = new int[FramesPerBlock * wavReader.Channels];

            int framesRead;
            while ((framesRead = wavReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * wavReader.Channels));
            }

            var durationSeconds = wavReader.SampleRate > 0 ? (double)wavReader.TotalSamples / wavReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeWavCodec(wavReader.BitsPerSample, wavReader.IsFloatFormat, wavReader.IsImaAdpcm, wavReader.IsALaw, wavReader.IsMuLaw);

            return new ProbeResult
            {
                FormatName = "wav",
                FormatLongName = "WAV / WAVE (Waveform Audio)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator.GetNormalizedWindows()
            };
        }

        private static ProbeResult ProbeAiff(string filePath)
        {
            using var aiffReader = AiffReader.Open(filePath);
            var waveformCalculator = new WaveformCalculator(aiffReader.TotalSamples, aiffReader.Channels, aiffReader.BitsPerSample);

            var buffer = new int[FramesPerBlock * aiffReader.Channels];

            int framesRead;
            while ((framesRead = aiffReader.ReadInterleavedSamples(buffer, FramesPerBlock)) > 0)
            {
                waveformCalculator.AddBlock(new ReadOnlySpan<int>(buffer, 0, framesRead * aiffReader.Channels));
            }

            var durationSeconds = aiffReader.SampleRate > 0 ? (double)aiffReader.TotalSamples / aiffReader.SampleRate : 0;
            var (codecName, codecLongName) = DescribeAiffCodec(aiffReader.BitsPerSample);

            return new ProbeResult
            {
                FormatName = "aiff",
                FormatLongName = "AIFF (Audio Interchange File Format)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator.GetNormalizedWindows()
            };
        }

        private static ProbeResult ProbeFlac(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = FlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "flac",
                FormatLongName = "FLAC (Free Lossless Audio Codec)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeMp3(string filePath)
        {
            var mp3ProbeResult = Mp3Probe.Probe(filePath);
            WaveformCalculator? waveformCalculator = null;

            Mp3Decoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            return new ProbeResult
            {
                FormatName = "mp3",
                FormatLongName = "MP3 (MPEG audio layer 3)",
                SizeBytes = GetFileSize(filePath),
                DurationSeconds = mp3ProbeResult.DurationSeconds,
                CodecType = "audio",
                CodecName = "mp3",
                CodecLongName = "MP3 (MPEG audio layer 3)",
                SampleRate = mp3ProbeResult.SampleRate,
                Channels = mp3ProbeResult.Channels,
                ChannelLayout = DescribeChannelLayout(mp3ProbeResult.Channels),
                BitsPerSample = 16,
                BitRate = mp3ProbeResult.BitRate,
                IsVariableBitRate = mp3ProbeResult.IsVariableBitRate,
                TimeBase = mp3ProbeResult.SampleRate > 0 ? $"1/{mp3ProbeResult.SampleRate}" : null,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeAac(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = AacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "aac",
                FormatLongName = "ADTS AAC (Advanced Audio Coding)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeAlac(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = AlacDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "caf",
                FormatLongName = "CAF (Core Audio Format)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeTta(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = TtaDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "tta",
                FormatLongName = "TTA (True Audio)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeOpus(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = OpusDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "ogg",
                FormatLongName = "Ogg",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeVorbis(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = VorbisDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "ogg",
                FormatLongName = "Ogg",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeWavPack(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = WavPackDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "wv",
                FormatLongName = "WavPack",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeWma(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;

            var streamInfo = WmaDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
            {
                waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                waveformCalculator.AddBlock(block);
            });

            var durationSeconds = streamInfo.SampleRate > 0 ? (double)streamInfo.TotalSamples / streamInfo.SampleRate : 0;

            return new ProbeResult
            {
                FormatName = "asf",
                FormatLongName = "ASF (Advanced / Active Streaming Format)",
                SizeBytes = GetFileSize(filePath),
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
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private static ProbeResult ProbeVideo(string filePath)
        {
            var movProbeResult = MovProbe.Probe(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var (formatName, formatLongName) = extension switch
            {
                ".mp4" => ("mp4", "MP4 (MPEG-4 Part 14)"),
                _ => ("mov", "QuickTime / MOV")
            };

            var audio = TryDecodeAudioTrack(filePath);

            return new ProbeResult
            {
                FormatName = formatName,
                FormatLongName = formatLongName,
                SizeBytes = GetFileSize(filePath),
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
                Waveform = audio?.Waveform
            };
        }

        private static DecodedAudioTrack? TryDecodeAudioTrack(string filePath)
        {
            WaveformCalculator? waveformCalculator = null;
            MovStreamInfo streamInfo;

            try
            {
                streamInfo = MovDecoder.Decode(filePath, (block, channels, sampleRate, bitsPerSample, totalSamples) =>
                {
                    waveformCalculator ??= new WaveformCalculator(totalSamples, channels, bitsPerSample);
                    waveformCalculator.AddBlock(block);
                });
            }
            catch (Exception)
            {
                // No audio track, or an audio codec/configuration this library doesn't decode
                // (stereo, non-AAC-LC, etc.) -- video-only metadata is still a useful probe result,
                // so this degrades gracefully instead of failing the whole probe.
                return null;
            }

            return new DecodedAudioTrack
            {
                SampleRate = streamInfo.SampleRate,
                Channels = streamInfo.Channels,
                BitsPerSample = streamInfo.BitsPerSample,
                TotalSamples = streamInfo.TotalSamples,
                Waveform = waveformCalculator?.GetNormalizedWindows() ?? []
            };
        }

        private sealed class DecodedAudioTrack
        {
            public required int SampleRate { get; init; }

            public required int Channels { get; init; }

            public required int BitsPerSample { get; init; }

            public required long TotalSamples { get; init; }

            public required IReadOnlyList<double> Waveform { get; init; }
        }

        private static (string CodecName, string CodecLongName) DescribeWavCodec(int bitsPerSample, bool isFloatFormat, bool isImaAdpcm, bool isALaw, bool isMuLaw)
        {
            if (isImaAdpcm)
            {
                return ("adpcm_ima_wav", "ADPCM IMA WAV");
            }

            if (isALaw)
            {
                return ("pcm_alaw", "PCM A-law / G.711 A-law");
            }

            if (isMuLaw)
            {
                return ("pcm_mulaw", "PCM mu-law / G.711 mu-law");
            }

            if (isFloatFormat)
            {
                return ("pcm_f32le", "PCM 32-bit floating-point little-endian");
            }

            return bitsPerSample switch
            {
                8 => ("pcm_u8", "PCM unsigned 8-bit"),
                16 => ("pcm_s16le", "PCM signed 16-bit little-endian"),
                24 => ("pcm_s24le", "PCM signed 24-bit little-endian"),
                32 => ("pcm_s32le", "PCM signed 32-bit little-endian"),
                _ => ($"pcm_s{bitsPerSample}le", $"PCM signed {bitsPerSample}-bit little-endian")
            };
        }

        private static (string CodecName, string CodecLongName) DescribeAiffCodec(int bitsPerSample)
        {
            return bitsPerSample switch
            {
                8 => ("pcm_s8", "PCM signed 8-bit"),
                16 => ("pcm_s16be", "PCM signed 16-bit big-endian"),
                24 => ("pcm_s24be", "PCM signed 24-bit big-endian"),
                32 => ("pcm_s32be", "PCM signed 32-bit big-endian"),
                _ => ($"pcm_s{bitsPerSample}be", $"PCM signed {bitsPerSample}-bit big-endian")
            };
        }

        private static string DescribeChannelLayout(int channels)
        {
            return channels switch
            {
                1 => "mono",
                2 => "stereo",
                _ => $"{channels} channels"
            };
        }

        private static long GetFileSize(string filePath)
        {
            return new FileInfo(filePath).Length;
        }
    }
}
