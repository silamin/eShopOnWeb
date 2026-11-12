using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using MediatR;
using Microsoft.eShopWeb.Web.Interfaces;
using Microsoft.eShopWeb.Web.Services;

namespace Microsoft.eShopWeb.Web.Configuration;

public static class ConfigureWebServices
{
    public static IServiceCollection AddWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMediatR(cfg => 
            cfg.RegisterServicesFromAssembly(typeof(BasketViewModelService).Assembly));
        services.AddScoped<IBasketViewModelService, BasketViewModelService>();
        services.AddScoped<CatalogViewModelService>();
        // Typed client to the Catalog service, called directly on the compose network (not via the gateway).
        // Polly: retry → circuit breaker → 1,5 s timeout per attempt (CatalogResilience). The client's own
        // timeout is the outer limit for all attempts together.
        var catalogBaseUrl = configuration["CatalogService:BaseUrl"]
            ?? throw new InvalidOperationException("CatalogService:BaseUrl is not configured.");
        services.AddSingleton<CatalogResilience>();
        services.AddTransient<CorrelationHandler>();
        services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
            {
                client.BaseAddress = new Uri(catalogBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(6);
            })
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddPolicyHandler((sp, _) => sp.GetRequiredService<CatalogResilience>().Retry)
            .AddPolicyHandler((sp, _) => sp.GetRequiredService<CatalogResilience>().CircuitBreaker)
            .AddPolicyHandler((sp, _) => sp.GetRequiredService<CatalogResilience>().Timeout);
        services.Configure<CatalogSettings>(configuration);
        services.AddScoped<ICatalogViewModelService, CachedCatalogViewModelService>();

        return services;
    }
}
