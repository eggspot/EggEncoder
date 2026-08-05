using BenchmarkDotNet.Attributes;
using EggEncoder.Codecs.Wav;

namespace EggEncoder.Benchmarks
{
    // Covers the WAV read/write path every codec bottlenecks through for probe/convert/cut.
    [MemoryDiagnoser]
    public class WavIoBenchmarks
    {
        private const int Channels = 2;
        private const int SampleRate = 44100;
        private const int TotalFrames = SampleRate * 10;
        private const int FramesPerBlock = 4096;

        private string _wavPath = null!;
        private int[] _pcm = null!;
        private int[] _readBuffer = null!;

        [GlobalSetup]
        public void Setup()
        {
            _pcm = new int[TotalFrames * Channels];
            var random = new Random(42);
            for (var i = 0; i < _pcm.Length; i++)
            {
                _pcm[i] = (short)random.Next(short.MinValue, short.MaxValue);
            }

            _readBuffer = new int[FramesPerBlock * Channels];
            _wavPath = Path.Combine(Path.GetTempPath(), $"bench_wav_{Guid.NewGuid():N}.wav");

            using var writer = WavWriter.Create(_wavPath, Channels, SampleRate, bitsPerSample: 16, TotalFrames);
            WriteAllBlocks(writer);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            File.Delete(_wavPath);
        }

        [Benchmark(Description = "WavReader: read 10s stereo/16-bit in 4096-frame blocks")]
        public long ReadAllBlocks()
        {
            using var reader = WavReader.Open(_wavPath);

            long totalFrames = 0;
            int framesRead;
            while ((framesRead = reader.ReadInterleavedSamples(_readBuffer, FramesPerBlock)) > 0)
            {
                totalFrames += framesRead;
            }

            return totalFrames;
        }

        [Benchmark(Description = "WavWriter: write 10s stereo/16-bit in 4096-frame blocks")]
        public void WriteAllBlocksBenchmark()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bench_wav_write_{Guid.NewGuid():N}.wav");
            try
            {
                using var writer = WavWriter.Create(path, Channels, SampleRate, bitsPerSample: 16, TotalFrames);
                WriteAllBlocks(writer);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private void WriteAllBlocks(WavWriter writer)
        {
            var block = new int[FramesPerBlock * Channels];

            for (var frameOffset = 0; frameOffset < TotalFrames; frameOffset += FramesPerBlock)
            {
                var framesThisBlock = Math.Min(FramesPerBlock, TotalFrames - frameOffset);
                Array.Copy(_pcm, frameOffset * Channels, block, 0, framesThisBlock * Channels);
                writer.WriteInterleavedSamples(block, framesThisBlock);
            }
        }
    }
}
