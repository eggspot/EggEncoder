using Microsoft.Extensions.DependencyInjection;

namespace EggEncoder
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddEggEncoder(this IServiceCollection services)
        {
            services.AddScoped<IMediaEncoder, NativeEncoder>();

            return services;
        }
    }
}
