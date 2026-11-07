using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.Web.Services;
using Xunit;

namespace Microsoft.eShopWeb.UnitTests.Web.Services;

public class CatalogClientTests
{
    private class StubHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (CatalogClient client, StubHandler handler) Create(TimeSpan? timeout = null)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://catalog/"), Timeout = timeout ?? TimeSpan.FromSeconds(3) };
        return (new CatalogClient(http), handler);
    }

    private const string TwoItems =
        "{\"catalogItems\":[{\"id\":1,\"name\":\"A\",\"description\":\"da\",\"price\":19.5,\"pictureUri\":\"/images/products/1.png\",\"catalogTypeId\":2,\"catalogBrandId\":2}," +
        "{\"id\":2,\"name\":\"B\",\"description\":\"db\",\"price\":8.5,\"pictureUri\":\"/images/products/2.png\",\"catalogTypeId\":1,\"catalogBrandId\":2}],\"pageCount\":2,\"totalItems\":12}";

    [Fact]
    public async Task GetItems_SendsPagingAndFilters_AndReturnsTotalCount()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json(TwoItems));

        var page = await client.GetItemsAsync(1, 10, brandId: 2, typeId: 3);

        Assert.Equal("/api/catalog-items?pageIndex=1&pageSize=10&catalogBrandId=2&catalogTypeId=3", handler.Urls.Single());
        Assert.Equal(12, page.TotalItems);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(19.5m, page.Items[0].Price);
    }

    [Fact]
    public async Task GetItem_Returns_Null_On404()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await client.GetItemAsync(999));
    }

    [Fact]
    public async Task GetItem_ReturnsItem()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json("{\"catalogItem\":{\"id\":3,\"name\":\"C\",\"description\":\"d\",\"price\":12,\"pictureUri\":\"p\",\"catalogTypeId\":2,\"catalogBrandId\":5}}"));
        var item = await client.GetItemAsync(3);
        Assert.Equal(12m, item!.Price);
        Assert.Equal("/api/catalog-items/3", handler.Urls.Single());
    }

    [Fact]
    public async Task GetItemsByIds_EmptyInput_MakesNoCall()
    {
        var (client, handler) = Create();
        var items = await client.GetItemsByIdsAsync(Array.Empty<int>());
        Assert.Empty(items);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task GetItemsByIds_IsOneBatchedCall_WithDistinctIds()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json(TwoItems));
        await client.GetItemsByIdsAsync(new[] { 1, 2, 1, 2 });
        Assert.Equal("/api/catalog-items/by-ids?ids=1&ids=2", handler.Urls.Single());
    }

    [Fact]
    public async Task GetItemsByIds_MoreThan200Ids_IsSplitIntoChunks()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json("{\"catalogItems\":[]}"));
        await client.GetItemsByIdsAsync(Enumerable.Range(1, 450));
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task ServerError_Throws_CatalogUnavailable()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetBrandsAsync());
    }

    [Fact]
    public async Task ConnectionRefused_Throws_CatalogUnavailable()
    {
        var (client, handler) = Create();
        handler.Respond = (_, _) => throw new HttpRequestException("connection refused");
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetTypesAsync());
    }

    [Fact]
    public async Task SlowCatalog_TimesOut_And_Throws_CatalogUnavailable()
    {
        var (client, handler) = Create(timeout: TimeSpan.FromMilliseconds(100));
        handler.Respond = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Json(TwoItems); };
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetItemsAsync(0, 10, null, null));
    }

    [Fact]
    public async Task CallerCancellation_IsNot_ReportedAsUnavailable()
    {
        var (client, handler) = Create();
        handler.Respond = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Json(TwoItems); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetItemsAsync(0, 10, null, null, cts.Token));
    }
}
