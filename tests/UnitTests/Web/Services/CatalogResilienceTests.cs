using System.Net;
using System.Text;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.Web.Services;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.eShopWeb.UnitTests.Web.Services;

/// <summary>
/// Krav 7: the Polly pipeline on Web → Catalog, built exactly as ConfigureWebServices builds it
/// (retry → circuit breaker → timeout per attempt), in front of a stub Catalog that counts calls.
/// </summary>
public class CatalogResilienceTests
{
    private sealed class StubCatalog : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _answers;
        private readonly HttpStatusCode _then;
        public int Calls;

        public StubCatalog(HttpStatusCode then, params HttpStatusCode[] first)
        {
            _answers = new Queue<HttpStatusCode>(first);
            _then = then;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var code = _answers.Count > 0 ? _answers.Dequeue() : _then;
            var body = code == HttpStatusCode.OK
                ? """{"catalogItems":[{"id":1,"name":".NET Bot Black Sweatshirt","description":"","price":19.5,"pictureUri":"","catalogTypeId":1,"catalogBrandId":1}],"totalItems":1}"""
                : "";
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static CatalogClient ClientOver(StubCatalog stub)
    {
        var policies = new CatalogResilience(NullLogger<CatalogResilience>.Instance);
        var pipeline = new PolicyHttpMessageHandler(policies.Retry)
        {
            InnerHandler = new PolicyHttpMessageHandler(policies.CircuitBreaker)
            {
                InnerHandler = new PolicyHttpMessageHandler(policies.Timeout) { InnerHandler = stub },
            },
        };
        return new CatalogClient(new HttpClient(pipeline) { BaseAddress = new Uri("http://catalog/") });
    }

    [Fact]
    public async Task A_transient_5xx_is_retried_and_the_page_gets_its_items()
    {
        var stub = new StubCatalog(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        var page = await ClientOver(stub).GetItemsAsync(0, 10, null, null);

        Assert.Equal(2, stub.Calls);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task A_404_is_an_answer_and_is_not_retried()
    {
        var stub = new StubCatalog(HttpStatusCode.NotFound);

        var item = await ClientOver(stub).GetItemAsync(42);

        Assert.Null(item);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Catalog_down_gives_1_plus_2_retries_then_CatalogUnavailable()
    {
        var stub = new StubCatalog(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => ClientOver(stub).GetItemsAsync(0, 10, null, null));

        Assert.Equal(1 + CatalogResilience.RetryCount, stub.Calls);
    }

    [Fact]
    public async Task After_5_failures_in_a_row_the_circuit_opens_and_Catalog_is_not_called()
    {
        var stub = new StubCatalog(HttpStatusCode.InternalServerError);
        var client = ClientOver(stub);

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetItemsAsync(0, 10, null, null)); // 3 calls
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetItemsAsync(0, 10, null, null)); // 2 more: open
        Assert.Equal(CatalogResilience.FailuresBeforeBreaking, stub.Calls);

        var open = await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetItemsAsync(0, 10, null, null));

        Assert.Contains("circuit open", open.Message);
        Assert.Equal(CatalogResilience.FailuresBeforeBreaking, stub.Calls);   // not called while open
    }
}
