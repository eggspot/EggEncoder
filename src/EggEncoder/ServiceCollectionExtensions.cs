using Microsoft.Extensions.DependencyInjection;

namespace EggEncoder
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddEggEncoder(this IServiceCollection services, Action<EggEncoderOptions> configure)
        {
            var options = new EggEncoderOptions();
            configure(options);

            if (options.UseNativeEncoder)
            {
                services.AddScoped<IMediaEncoder, NativeEncoder>();
                return services;
            }

            var ffmpegBinPath = options.FfmpegBinPath ?? throw new ArgumentNullException(nameof(options.FfmpegBinPath), $"{nameof(EggEncoderOptions.FfmpegBinPath)} must be set when {nameof(EggEncoderOptions.UseNativeEncoder)} is false");

            services.AddSingleton(new FfmpegEncoderConfiguration { FfmpegBinPath = ffmpegBinPath });
            services.AddSingleton<IFfmpegEncoderBinFactory, FfmpegEncoderBinFactory>();
            services.AddScoped<IMediaEncoder, FfmpegEncoder>();

            return services;
        }
    }

    public class EggEncoderOptions
    {
        public bool UseNativeEncoder { get; set; }

        public string? FfmpegBinPath { get; set; }
    }
}
