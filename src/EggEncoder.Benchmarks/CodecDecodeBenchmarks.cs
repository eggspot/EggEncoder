using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs.Flac;
using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Tta;
using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;

namespace EggEncoder.Benchmarks
{
    // Pure decode throughput for this project's own from-scratch (no native binary, no external
    // process) codec rewrites -- docs/managed-codec-rewrite-plan.md's own FLAC/WavPack/TTA, plus
    // MP3 (still NLayer-backed on decode; see docs/external-dependency-removal-plan.md). Unlike
    // AudioCutterBenchmarks' own "Convert" benchmarks (which measure WAV decode + MP3/FLAC encode
    // together), this isolates each codec's own decode alone, and covers WavPack/TTA, which that
    // file doesn't benchmark at all.
    [MemoryDiagnoser]
    public class CodecDecodeBenchmarks
    {
        private const int Channels = 2;
        private const int SampleRate = 44100;
        private const int TotalFrames = SampleRate * 10;

        private string _flacPath = null!;
        private string _mp3Path = null!;
        private string _wavPackPath = null!;
        private string _ttaPath = null!;

        [GlobalSetup]
        public void Setup()
        {
            var pcm = new int[TotalFrames * Channels];
            for (var frame = 0; frame < TotalFrames; frame++)
            {
                var sample = (short)(10000 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
                pcm[frame * Channels] = sample;
                pcm[(frame * Channels) + 1] = sample;
            }

            var wavPath = Path.Combine(Path.GetTempPath(), $"bench_decode_source_{Guid.NewGuid():N}.wav");
            using (var writer = WavWriter.Create(wavPath, Channels, SampleRate, bitsPerSample: 16, TotalFrames))
            {
                writer.WriteInterleavedSamples(pcm, TotalFrames);
            }

            try
            {
                _flacPath = Path.Combine(Path.GetTempPath(), $"bench_decode_{Guid.NewGuid():N}.flac");
                FlacEncoder.Encode(wavPath, _flacPath);

                _mp3Path = Path.Combine(Path.GetTempPath(), $"bench_decode_{Guid.NewGuid():N}.mp3");
                Mp3Encoder.Encode(wavPath, _mp3Path);

                _wavPackPath = Path.Combine(Path.GetTempPath(), $"bench_decode_{Guid.NewGuid():N}.wv");
                WavPackEncoder.Encode(wavPath, _wavPackPath);

                _ttaPath = Path.Combine(Path.GetTempPath(), $"bench_decode_{Guid.NewGuid():N}.tta");
                TtaEncoder.Encode(wavPath, _ttaPath);
            }
            finally
            {
                File.Delete(wavPath);
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            File.Delete(_flacPath);
            File.Delete(_mp3Path);
            File.Delete(_wavPackPath);
            File.Delete(_ttaPath);
        }

        [Benchmark(Description = "FlacDecoder: decode 10s stereo/16-bit")]
        public long DecodeFlac()
        {
            long total = 0;
            FlacDecoder.Decode(_flacPath, (block, _, _, _, _) => total += block.Length);
            return total;
        }

        [Benchmark(Description = "Mp3Decoder: decode 10s stereo/16-bit (NLayer-backed)")]
        public long DecodeMp3()
        {
            long total = 0;
            Mp3Decoder.Decode(_mp3Path, (block, _, _, _, _) => total += block.Length);
            return total;
        }

        [Benchmark(Description = "WavPackDecoder: decode 10s stereo/16-bit")]
        public long DecodeWavPack()
        {
            long total = 0;
            WavPackDecoder.Decode(_wavPackPath, (block, _, _, _, _) => total += block.Length);
            return total;
        }

        [Benchmark(Description = "TtaDecoder: decode 10s stereo/16-bit")]
        public long DecodeTta()
        {
            long total = 0;
            TtaDecoder.Decode(_ttaPath, (block, _, _, _, _) => total += block.Length);
            return total;
        }
    }
}
