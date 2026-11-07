using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.eShopWeb.ApplicationCore.Interfaces;

public record CatalogItemInfo(
    int Id,
    string Name,
    string Description,
    decimal Price,
    string PictureUri,
    int CatalogTypeId,
    int CatalogBrandId);

public record CatalogLookupInfo(int Id, string Name);

public record CatalogPageInfo(IReadOnlyList<CatalogItemInfo> Items, int TotalItems);

/// <summary>
/// The monolith's single, read-only seam to the Catalog service. Every catalog read in the
/// monolith goes through this interface. Writes (create/update/delete) go through the gateway
/// to the Catalog service's admin API, never through here.
/// Throws <see cref="Exceptions.CatalogUnavailableException"/> when the Catalog service cannot be reached.
/// </summary>
public interface ICatalogClient
{
    Task<CatalogPageInfo> GetItemsAsync(int pageIndex, int pageSize, int? brandId, int? typeId, CancellationToken ct = default);

    /// <summary>Returns null when the item does not exist.</summary>
    Task<CatalogItemInfo?> GetItemAsync(int id, CancellationToken ct = default);

    /// <summary>One batched call. Ids that do not exist are simply missing from the result.</summary>
    Task<IReadOnlyList<CatalogItemInfo>> GetItemsByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogLookupInfo>> GetBrandsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CatalogLookupInfo>> GetTypesAsync(CancellationToken ct = default);
}
