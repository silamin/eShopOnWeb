using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.eShopWeb.Web.HealthChecks;

/// <summary>
/// Is the catalog the shop depends on reachable? The original eShopOnWeb check fetched ApiBase
/// ("http://localhost:5100/api/"), the browser's address: inside the container that is the container
/// itself, so /health was always 503 under docker compose. This check asks the Catalog service through
/// the same typed client (and the same Polly policies) the pages use, on the compose network.
/// The check's name "api_health_check" is kept so existing probes still match.
/// </summary>
public class ApiHealthCheck : IHealthCheck
{
    private readonly ICatalogClient _catalog;

    public ApiHealthCheck(ICatalogClient catalog) => _catalog = catalog;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _catalog.GetItemsAsync(0, 1, null, null, cancellationToken);
            return page.TotalItems > 0
                ? HealthCheckResult.Healthy($"Catalog service answered: {page.TotalItems} items.")
                : HealthCheckResult.Degraded("Catalog service answered, but the catalog is empty.");
        }
        catch (CatalogUnavailableException ex)
        {
            return HealthCheckResult.Unhealthy("Catalog service unavailable: " + ex.Message);
        }
    }
}
