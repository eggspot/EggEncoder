using EggEncoder.Codecs.Mp3;
using EggEncoder.Codecs.Wav;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EggEncoder.UnitTests
{
    public class EggEncoderOptionsTest
    {
        [Fact]
        public void MaxDegreeOfParallelism_Below_One_Should_Be_Rejected()
        {
            var options = new EggEncoderOptions();

            var setZero = () => options.MaxDegreeOfParallelism = 0;

            setZero.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void A_Codec_Parallelism_Scope_Should_Set_The_Mp3_Encoder_Default_And_Restore_It()
        {
            var before = Mp3Encoder.DefaultMaxDegreeOfParallelism;

            using (CodecParallelism.Limit(1))
            {
                Mp3Encoder.DefaultMaxDegreeOfParallelism.Should().Be(1);
                using (CodecParallelism.Limit(null))
                {
                    Mp3Encoder.DefaultMaxDegreeOfParallelism.Should().Be(1);
                }
            }

            Mp3Encoder.DefaultMaxDegreeOfParallelism.Should().Be(before);
        }

        [Fact]
        public async Task Converting_With_One_Thread_Should_Write_The_Same_Mp3_As_The_Default()
        {
            var source = WriteTone();
            var oneThread = Path.ChangeExtension(source, ".one.mp3");
            var byDefault = Path.ChangeExtension(source, ".default.mp3");
            try
            {
                await new NativeEncoder(NullLogger<NativeEncoder>.Instance, new EggEncoderOptions { MaxDegreeOfParallelism = 1 }).ConvertFile(source, oneThread);
                await new NativeEncoder(NullLogger<NativeEncoder>.Instance).ConvertFile(source, byDefault);

                File.ReadAllBytes(oneThread).Should().Equal(File.ReadAllBytes(byDefault));
            }
            finally
            {
                File.Delete(source);
                File.Delete(oneThread);
                File.Delete(byDefault);
            }
        }

        [Fact]
        public void AddEggEncoder_With_Options_Should_Register_A_Configured_Encoder()
        {
            var services = new ServiceCollection();

            services.AddEggEncoder(options =>
            {
                options.EnableLogging = false;
                options.MaxDegreeOfParallelism = 2;
            });

            var encoder = services.Single(service => service.ServiceType == typeof(NativeEncoder)).ImplementationFactory!(new LoggerOnlyServiceProvider());
            encoder.Should().BeOfType<NativeEncoder>();
            services.Should().Contain(service => service.ServiceType == typeof(IMediaEncoder) && service.Lifetime == ServiceLifetime.Scoped);
            services.Should().Contain(service => service.ServiceType == typeof(IPcmTransformEncoder) && service.Lifetime == ServiceLifetime.Scoped);
        }

        [Fact]
        public async Task Converting_To_A_Bare_File_Name_Should_Write_It_In_The_Current_Directory()
        {
            var source = WriteTone();
            var destination = $"bare_{Guid.NewGuid():N}.mp3";
            try
            {
                await new NativeEncoder(NullLogger<NativeEncoder>.Instance).ConvertFile(source, destination);

                File.Exists(Path.Combine(Directory.GetCurrentDirectory(), destination)).Should().BeTrue();
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        private sealed class LoggerOnlyServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(Microsoft.Extensions.Logging.ILogger<NativeEncoder>) ? NullLogger<NativeEncoder>.Instance : null;
        }

        private static string WriteTone()
        {
            const int sampleRate = 44100;
            var frames = sampleRate * 2;
            var samples = new int[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                samples[i * 2] = (int)(9000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                samples[(i * 2) + 1] = (int)(7000 * Math.Sin(2 * Math.PI * 660 * i / sampleRate));
            }

            var path = Path.Combine(Path.GetTempPath(), $"options_{Guid.NewGuid():N}.wav");
            using var writer = WavWriter.Create(path, 2, sampleRate, 16, frames);
            writer.WriteInterleavedSamples(samples, frames);
            writer.Finish();
            return path;
        }
    }
}
