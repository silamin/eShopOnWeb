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
        // Plain 3 s timeout for now; the Polly retry + circuit breaker is added to THIS registration on Mon 16.
        var catalogBaseUrl = configuration["CatalogService:BaseUrl"]
            ?? throw new InvalidOperationException("CatalogService:BaseUrl is not configured.");
        services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
        {
            client.BaseAddress = new Uri(catalogBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.Configure<CatalogSettings>(configuration);
        services.AddScoped<ICatalogViewModelService, CachedCatalogViewModelService>();

        return services;
    }
}
