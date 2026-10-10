using EggEncoder.Codecs.Wav;
using EggEncoder.Codecs.WavPack;
using EggEncoder.UnitTests.TestUtilities;
using FluentAssertions;

namespace EggEncoder.UnitTests.Codecs.WavPack
{
    public class WavPackFfmpegCrossCheckTest
    {
        private static readonly string _wavFixturePath = Path.GetFullPath("Codecs/WavPack/sample.wav");
        private static readonly string _ffmpegWvFixturePath = Path.GetFullPath("Codecs/WavPack/sample_ffmpeg.wv");

        [Fact]
        public void Decode_FfmpegProducedWavPack_Should_Match_Original_Wav_Samples()
        {
            var expectedSamples = ReadAllSamples(_wavFixturePath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(_ffmpegWvFixturePath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        [Fact]
        public void Decode_FfmpegProducedMonoWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_mono_ffmpeg.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_mono_ffmpeg.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WavPack's independent-channel stereo decorrelation terms (unlike sample.wav/
        // sample_ffmpeg.wv above, which ffmpeg encodes as joint/mid-side stereo by default).
        [Fact]
        public void Decode_FfmpegProducedIndependentStereoWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_stereo_indep_ffmpeg.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_stereo_indep_ffmpeg.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises the 24-bit sample path (every other fixture above is 16-bit), encoded with the
        // official reference wavpack CLI rather than ffmpeg (whose own encoder only emits 8/16/32-bit
        // containers, never a real 24-bit one). Uses genuinely distinct left/right tones so the
        // reference encoder doesn't take the "false stereo" shortcut exercised separately below.
        [Fact]
        public void Decode_ReferenceEncoder24BitStereoWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_24bit_stereo_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_24bit_stereo_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            streamInfo.BitsPerSample.Should().Be(24);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WavPack's "false stereo" block flag: a stereo-flagged block whose two channels
        // happen to be identical, so the reference encoder only transmits one channel's worth of
        // decorrelation/entropy data and the decoder must duplicate it into both outputs.
        [Fact]
        public void Decode_ReferenceEncoderFalseStereoWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_false_stereo_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_false_stereo_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises the zero-run-length entropy-coding shortcut with a genuinely long run of exact
        // digital silence (5000 samples) -- the shortcut only engages once the per-channel median
        // has decayed low enough, and critically, the sample immediately AFTER the run ends must be
        // decoded normally rather than mistaken for the start of another zero run.
        [Fact]
        public void Decode_ReferenceEncoderSilenceRunWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_silence_run_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_silence_run_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WP_ID_INT32_INFO's "And=1, Or=0" bit-filling variant (byte 3 of its payload):
        // the newly-shifted-in low bit is reconstructed as a copy of the sample's own low bit,
        // rather than a plain zero-filled shift -- confirmed to occur for ordinary 16-bit lossless
        // content with genuine full-scale (int16.MinValue/MaxValue) samples, not just the
        // hybrid/>24-bit-integer cases general WavPack documentation describes it for.
        [Fact]
        public void Decode_ReferenceEncoderFullScaleWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_fullscale_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_fullscale_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WP_ID_INT32_INFO's "And=1, Or=1" bit-filling variant (byte 2 of its payload):
        // the newly-shifted-in low bit is always reconstructed as one, regardless of the sample's
        // own value -- a genuine reference-encoder-produced file of repeated int16.MaxValue samples
        // triggers this specific byte rather than byte 3's parity-based one.
        [Fact]
        public void Decode_ReferenceEncoderFullScaleAllOnesWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_fullscale_allones_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_fullscale_allones_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WavPack's multi-block-per-frame sequence: a stereo stream with no explicit
        // channel layout (matching what this project's own native WavPackEncoderSession produces,
        // since it never sets WavpackConfig's ChannelMask) is split by the reference encoder into
        // two single-channel blocks per frame (initial + final) rather than one combined block.
        [Fact]
        public void Decode_ReferenceEncoderSplitMonoBlocksWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_split_mono_blocks_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_split_mono_blocks_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        // Exercises WP_ID_SAMPLE_RATE metadata: a non-standard sample rate (not one of WavPack's 15
        // standard-rate-table entries) is carried as a plain 24-bit value in this sub-block instead
        // of the block header's own 4-bit rate index.
        [Fact]
        public void Decode_ReferenceEncoderNonStandardSampleRateWavPack_Should_Match_Original_Wav_Samples()
        {
            var wavPath = Path.GetFullPath("Codecs/WavPack/sample_nonstandard_rate_wavpack.wav");
            var wvPath = Path.GetFullPath("Codecs/WavPack/sample_nonstandard_rate_wavpack.wv");
            var expectedSamples = ReadAllSamples(wavPath, out var channels, out var sampleRate);

            var (streamInfo, decodedSamples) = WavPackTestDecoder.DecodeAll(wvPath);

            streamInfo.Channels.Should().Be(channels);
            streamInfo.SampleRate.Should().Be(sampleRate);
            decodedSamples.Should().Equal(expectedSamples);
        }

        [Fact]
        public void Encode_SameSourceAsFfmpeg_Should_Also_Reproduce_Exact_Original_Samples()
        {
            var expectedSamples = ReadAllSamples(_wavFixturePath, out _, out _);

            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var nativeWvPath = Path.Combine(tempDirectory, "native.wv");
                WavPackEncoder.Encode(_wavFixturePath, nativeWvPath);

                var (_, decodedSamples) = WavPackTestDecoder.DecodeAll(nativeWvPath);

                decodedSamples.Should().Equal(expectedSamples);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        private static int[] ReadAllSamples(string wavFilePath, out int channels, out int sampleRate)
        {
            using var wavReader = WavReader.Open(wavFilePath);
            channels = wavReader.Channels;
            sampleRate = wavReader.SampleRate;

            var samples = new int[wavReader.TotalSamples * wavReader.Channels];
            wavReader.ReadInterleavedSamples(samples, (int)wavReader.TotalSamples);

            return samples;
        }
    }
}
