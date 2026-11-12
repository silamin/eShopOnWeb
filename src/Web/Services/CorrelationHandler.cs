using Eshop.Messaging;

namespace Microsoft.eShopWeb.Web.Services;

/// <summary>Passes the current correlation id on to the Catalog service, so its log lines carry the same id.</summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Correlation.Current is { } id && !request.Headers.Contains(Correlation.Header))
            request.Headers.Add(Correlation.Header, id);
        return base.SendAsync(request, ct);
    }
}
