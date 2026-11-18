using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EggEncoder
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddEggEncoder(this IServiceCollection services, bool enableLogging = true)
        {
            return Register(services, serviceProvider =>
                new NativeEncoder(serviceProvider.GetRequiredService<ILogger<NativeEncoder>>(), enableLogging));
        }

        /// <summary>
        /// Registers <see cref="NativeEncoder"/> (scoped) as <see cref="IMediaEncoder"/> and
        /// <see cref="IPcmTransformEncoder"/>, configured by <paramref name="configure"/>.
        /// </summary>
        public static IServiceCollection AddEggEncoder(this IServiceCollection services, Action<EggEncoderOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new EggEncoderOptions();
            configure(options);
            return Register(services, serviceProvider =>
                new NativeEncoder(serviceProvider.GetRequiredService<ILogger<NativeEncoder>>(), options));
        }

        private static IServiceCollection Register(IServiceCollection services, Func<IServiceProvider, NativeEncoder> create)
        {
            services.AddScoped(create);
            services.AddScoped<IMediaEncoder>(serviceProvider => serviceProvider.GetRequiredService<NativeEncoder>());
            services.AddScoped<IPcmTransformEncoder>(serviceProvider => serviceProvider.GetRequiredService<NativeEncoder>());

            return services;
        }
    }
}
