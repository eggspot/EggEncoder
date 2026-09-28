using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EggEncoder
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddEggEncoder(this IServiceCollection services, bool enableLogging = true)
        {
            services.AddScoped(serviceProvider =>
                new NativeEncoder(serviceProvider.GetRequiredService<ILogger<NativeEncoder>>(), enableLogging));

            services.AddScoped<IMediaEncoder>(serviceProvider => serviceProvider.GetRequiredService<NativeEncoder>());
            services.AddScoped<IPcmTransformEncoder>(serviceProvider => serviceProvider.GetRequiredService<NativeEncoder>());

            return services;
        }
    }
}
