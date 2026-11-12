using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.eShopWeb.Web.HealthChecks;

/// <summary>
/// Does the home page render with catalog items? The original check fetched the address from the
/// request's Host header — behind the gateway that is "localhost:5100", which inside the container
/// points at the container itself, not the gateway, so /health was always 503 under docker compose.
/// This check calls the address this app instance actually listens on (e.g. http://+:8080 → localhost:8080).
/// </summary>
public class HomePageHealthCheck : IHealthCheck
{
    private readonly IServer _server;
    private readonly IHttpClientFactory _httpClients;

    public HomePageHealthCheck(IServer server, IHttpClientFactory httpClients)
    {
        _server = server;
        _httpClients = httpClients;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var self = OwnAddress();
        if (self is null) return HealthCheckResult.Unhealthy("No listening address found.");

        var client = _httpClients.CreateClient(nameof(HomePageHealthCheck));
        client.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            var pageContents = await client.GetStringAsync(self, cancellationToken);
            return pageContents.Contains(".NET Bot Black Sweatshirt")
                ? HealthCheckResult.Healthy($"Home page at {self} lists catalog items.")
                : HealthCheckResult.Unhealthy($"Home page at {self} rendered without catalog items.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy($"Home page at {self} did not answer: {ex.Message}");
        }
    }

    private string? OwnAddress()
    {
        var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? _server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is null) return null;
        var uri = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost")
            .Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"));
        return uri.Uri.ToString();
    }
}
