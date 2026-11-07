using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;

namespace Microsoft.eShopWeb.Web.Services;

/// <summary>
/// Typed HttpClient to the Catalog service, called directly on the compose network (not through the
/// gateway). Reads are open, so no token is sent. The timeout is set where the client is registered
/// (ConfigureWebServices), which is also where a resilience handler is added later.
/// </summary>
public class CatalogClient : ICatalogClient
{
    private const int MaxIdsPerCall = 200;   // the Catalog service's by-ids limit
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public CatalogClient(HttpClient http) => _http = http;

    public async Task<CatalogPageInfo> GetItemsAsync(int pageIndex, int pageSize, int? brandId, int? typeId, CancellationToken ct = default)
    {
        var url = $"api/catalog-items?pageIndex={pageIndex}&pageSize={pageSize}";
        if (brandId.HasValue) url += $"&catalogBrandId={brandId}";
        if (typeId.HasValue) url += $"&catalogTypeId={typeId}";
        var r = await GetAsync<ItemsResponse>(url, ct);
        return new CatalogPageInfo(r.CatalogItems.Select(ToInfo).ToList(), r.TotalItems);
    }

    public async Task<CatalogItemInfo?> GetItemAsync(int id, CancellationToken ct = default)
    {
        var r = await GetAsync<ItemResponse>($"api/catalog-items/{id}", ct, notFoundIsNull: true);
        return r?.CatalogItem is null ? null : ToInfo(r.CatalogItem);
    }

    public async Task<IReadOnlyList<CatalogItemInfo>> GetItemsByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var distinct = ids.Distinct().ToList();
        var result = new List<CatalogItemInfo>();
        foreach (var chunk in distinct.Chunk(MaxIdsPerCall))
        {
            var url = "api/catalog-items/by-ids?" + string.Join("&", chunk.Select(i => $"ids={i}"));
            var r = await GetAsync<ItemsResponse>(url, ct);
            result.AddRange(r.CatalogItems.Select(ToInfo));
        }
        return result;
    }

    public async Task<IReadOnlyList<CatalogLookupInfo>> GetBrandsAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<BrandsResponse>("api/catalog-brands", ct);
        return r.CatalogBrands.Select(b => new CatalogLookupInfo(b.Id, b.Name)).ToList();
    }

    public async Task<IReadOnlyList<CatalogLookupInfo>> GetTypesAsync(CancellationToken ct = default)
    {
        var r = await GetAsync<TypesResponse>("api/catalog-types", ct);
        return r.CatalogTypes.Select(t => new CatalogLookupInfo(t.Id, t.Name)).ToList();
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct) where T : class
        => (await GetAsync<T>(url, ct, notFoundIsNull: false))!;

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct, bool notFoundIsNull) where T : class
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new CatalogUnavailableException($"Catalog service answered {(int)response.StatusCode} for {url}.");
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                   ?? throw new CatalogUnavailableException($"Catalog service sent an empty body for {url}.");
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogUnavailableException("Catalog service could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CatalogUnavailableException("Catalog service did not answer in time.", ex);   // HttpClient timeout
        }
        catch (JsonException ex)
        {
            throw new CatalogUnavailableException("Catalog service sent a response that could not be read.", ex);
        }
    }

    private static CatalogItemInfo ToInfo(ItemDto d)
        => new(d.Id, d.Name ?? "", d.Description ?? "", d.Price, d.PictureUri ?? "", d.CatalogTypeId, d.CatalogBrandId);

    // Wire shapes of the Catalog service (same as the Public API's catalog endpoints, plus totalItems).
    private record ItemDto(int Id, string? Name, string? Description, decimal Price, string? PictureUri, int CatalogTypeId, int CatalogBrandId);
    private record ItemsResponse(List<ItemDto> CatalogItems, int PageCount = 0, int TotalItems = 0);
    private record ItemResponse(ItemDto? CatalogItem);
    private record LookupDto(int Id, string Name);
    private record BrandsResponse(List<LookupDto> CatalogBrands);
    private record TypesResponse(List<LookupDto> CatalogTypes);
}
