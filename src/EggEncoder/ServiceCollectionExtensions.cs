using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EggEncoder
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddEggEncoder(this IServiceCollection services, bool enableLogging = true)
        {
            services.AddScoped<IMediaEncoder>(serviceProvider =>
                new NativeEncoder(serviceProvider.GetRequiredService<ILogger<NativeEncoder>>(), enableLogging));

            return services;
        }
    }
}
