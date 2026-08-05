using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs;
using EggEncoder.Codecs.Wav;

namespace EggEncoder.Benchmarks
{
    // End-to-end throughput for the decode-block -> encode-block pipeline used by
    // NativeEncoder.ConvertFile/CutFile -- the path a real-time transcode call actually runs.
    [MemoryDiagnoser]
    public class AudioCutterBenchmarks
    {
        private const int Channels = 2;
        private const int SampleRate = 44100;
        private const int TotalFrames = SampleRate * 10;

        private string _wavPath = null!;
        private string _destMp3Path = null!;
        private string _destFlacPath = null!;

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

            _wavPath = Path.Combine(Path.GetTempPath(), $"bench_cutter_source_{Guid.NewGuid():N}.wav");
            using (var writer = WavWriter.Create(_wavPath, Channels, SampleRate, bitsPerSample: 16, TotalFrames))
            {
                writer.WriteInterleavedSamples(pcm, TotalFrames);
            }

            _destMp3Path = Path.Combine(Path.GetTempPath(), $"bench_cutter_dest_{Guid.NewGuid():N}.mp3");
            _destFlacPath = Path.Combine(Path.GetTempPath(), $"bench_cutter_dest_{Guid.NewGuid():N}.flac");
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            File.Delete(_wavPath);
            File.Delete(_destMp3Path);
            File.Delete(_destFlacPath);
        }

        [Benchmark(Description = "Convert 10s WAV -> MP3")]
        public void ConvertWavToMp3()
        {
            AudioCutter.Convert(_wavPath, _destMp3Path);
        }

        [Benchmark(Description = "Convert 10s WAV -> FLAC")]
        public void ConvertWavToFlac()
        {
            AudioCutter.Convert(_wavPath, _destFlacPath);
        }
    }
}
