using EggEncoder.Codecs.Wav;
using EggEncoder.Pcm;
using FluentAssertions;

namespace EggEncoder.UnitTests.Pcm
{
    public class PcmAudioTest
    {
        public static readonly string VideoAacFixture = Path.GetFullPath("Pcm/video_aac.mp4");
        public static readonly string VideoAacFfmpegDecode = Path.GetFullPath("Pcm/video_aac_ffmpeg.pcm");
        public static readonly string DelayedAacFixture = Path.GetFullPath("Pcm/delayed_aac.mp4");
        public static readonly string DelayedAacFfmpegDecode = Path.GetFullPath("Pcm/delayed_aac_ffmpeg.pcm");

        [Fact]
        public void Constructor_Should_Wrap_The_Array_Without_Copying()
        {
            var samples = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };

            var audio = new PcmAudio(samples, 2, 8000);

            audio.Samples.Should().BeSameAs(samples);
            audio.FrameCount.Should().Be(2);
            audio.Duration.Should().Be(TimeSpan.FromSeconds(2 / 8000.0));
        }

        [Theory]
        [InlineData(0, 8000)]
        [InlineData(1, 0)]
        public void Constructor_InvalidFormat_Should_Throw_ArgumentOutOfRange(int channels, int sampleRate)
        {
            var act = () => new PcmAudio([], channels, sampleRate);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_ThreeChannels_Should_Throw_NotSupported()
        {
            var act = () => new PcmAudio(new float[3], 3, 8000);

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Constructor_PartialFrame_Should_Throw_Argument()
        {
            var act = () => new PcmAudio(new float[3], 2, 8000);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_NullSamples_Should_Throw_ArgumentNull()
        {
            var act = () => new PcmAudio(null!, 1, 8000);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Silence_Should_Hold_Zeros_For_The_Rounded_Frame_Count()
        {
            var audio = PcmAudio.Silence(2, 48000, TimeSpan.FromMilliseconds(10.01));

            audio.FrameCount.Should().Be(480);
            audio.Samples.Should().OnlyContain(s => s == 0f);
        }

        [Fact]
        public void Silence_NegativeDuration_Should_Throw_ArgumentOutOfRange()
        {
            var act = () => PcmAudio.Silence(1, 8000, TimeSpan.FromSeconds(-1));

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void ConvertTo_SameFormat_Should_Return_The_Same_Instance()
        {
            var audio = new PcmAudio([0.5f], 1, 8000);

            audio.ConvertTo(8000, 1).Should().BeSameAs(audio);
        }

        [Fact]
        public void ConvertTo_StereoToMono_Should_Average_And_Keep_Values_Above_One()
        {
            var audio = new PcmAudio([1.5f, 0.5f, -0.2f, 0.4f], 2, 8000);

            var mono = audio.ConvertTo(8000, 1);

            mono.Samples.Should().Equal(1.0f, 0.1f);
        }

        [Fact]
        public void ConvertTo_MonoToStereo_Should_Copy_The_Channel()
        {
            var audio = new PcmAudio([1.25f, -0.5f], 1, 8000);

            audio.ConvertTo(8000, 2).Samples.Should().Equal(1.25f, 1.25f, -0.5f, -0.5f);
        }

        [Fact]
        public void ConvertTo_ResampledTone_Should_Keep_Its_Length_And_Amplitude_Unclipped()
        {
            var audio = Tone(44100, 1, 440, 1.5, 44100);

            var resampled = audio.ConvertTo(48000, 2);

            resampled.FrameCount.Should().Be(48000);
            var middle = resampled.Samples.Skip(48000).Take(48000).Max();
            middle.Should().BeApproximately(1.5f, 0.01f);
        }

        [Fact]
        public void DownmixForSpeech_Should_Give_Mono_16k()
        {
            var speech = Tone(48000, 2, 300, 0.5, 48000).DownmixForSpeech();

            speech.Channels.Should().Be(1);
            speech.SampleRate.Should().Be(16000);
            speech.FrameCount.Should().Be(16000);
        }

        [Fact]
        public void ToInt16_Should_Round_Clip_And_Zero_NaN()
        {
            var audio = new PcmAudio([0.5f, 2f, -2f, float.NaN, 1f / 32767f], 1, 8000);

            audio.ToInt16().Should().Equal(16384, short.MaxValue, short.MinValue, 0, 1);
        }

        [Fact]
        public void Decode_Wav16_Should_Round_Trip_Through_ToInt16()
        {
            var path = Path.Combine(Path.GetTempPath(), $"eggencoder-pcm-{Guid.NewGuid():N}.wav");
            var samples = new int[] { 0, 1, -1, 32767, -32768, 1234, -4321, 7 };
            try
            {
                using (var writer = WavWriter.Create(path, 2, 22050, 16, samples.Length / 2))
                {
                    writer.WriteInterleavedSamples(samples, samples.Length / 2);
                    writer.Finish();
                }

                var audio = PcmAudio.Decode(path);

                audio.Channels.Should().Be(2);
                audio.SampleRate.Should().Be(22050);
                audio.ToInt16().Select(s => (int)s).Should().Equal(0, 1, -1, 32767, -32768, 1234, -4321, 7);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Decode_Mp4WithVideo_Should_Drop_Priming_And_Trim_To_The_Edit_List()
        {
            var audio = PcmAudio.Decode(VideoAacFixture);

            audio.Channels.Should().Be(1);
            audio.SampleRate.Should().Be(48000);
            audio.FrameCount.Should().Be(12000);
            var ours = audio.ToInt16();
            var ffmpeg = ReadPcm(VideoAacFfmpegDecode);
            ours.Zip(ffmpeg).Max(pair => Math.Abs(pair.First - pair.Second)).Should().BeLessThanOrEqualTo(3);
        }

        [Fact]
        public void Decode_Mp4WithLeadingEmptyEdit_Should_Start_With_Silence()
        {
            var audio = PcmAudio.Decode(DelayedAacFixture);

            audio.FrameCount.Should().Be(3744 + 13056);
            var ours = audio.ToInt16();
            ours.Take(3744).Should().OnlyContain(s => s == 0);
            var ffmpeg = ReadPcm(DelayedAacFfmpegDecode);
            ours.Skip(3744).Zip(ffmpeg).Max(pair => Math.Abs(pair.First - pair.Second)).Should().BeLessThanOrEqualTo(3);
        }

        [Fact]
        public void Decode_Mp4WithoutAudio_Should_Throw_InvalidData()
        {
            var act = () => PcmAudio.Decode(EggEncoder.UnitTests.Containers.Mp4.Mp4FileSourceTest.HevcFixture);

            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Decode_UnknownExtension_Should_Throw_NotSupported()
        {
            var act = () => PcmAudio.Decode("audio.xyz");

            act.Should().Throw<NotSupportedException>();
        }

        internal static PcmAudio Tone(int sampleRate, int channels, double frequency, double amplitude, int frames)
        {
            var samples = new float[frames * channels];
            for (var n = 0; n < frames; n++)
            {
                for (var c = 0; c < channels; c++)
                {
                    samples[(n * channels) + c] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * n / sampleRate));
                }
            }

            return new PcmAudio(samples, channels, sampleRate);
        }

        private static short[] ReadPcm(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }
    }
}
