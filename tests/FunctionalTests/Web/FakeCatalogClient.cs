using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;

namespace Microsoft.eShopWeb.FunctionalTests.Web;

/// <summary>
/// In-memory stand-in for the Catalog service in the monolith's functional tests: the same 12 items
/// (same ids) the Catalog service seeds, so the pages render without a running Catalog service.
/// </summary>
public class FakeCatalogClient : ICatalogClient
{
    private static readonly List<CatalogItemInfo> Items = new()
    {
        new(1, ".NET Bot Black Sweatshirt", ".NET Bot Black Sweatshirt", 19.5M, "/images/products/1.png", 2, 2),
        new(2, ".NET Black & White Mug", ".NET Black & White Mug", 8.50M, "/images/products/2.png", 1, 2),
        new(3, "Prism White T-Shirt", "Prism White T-Shirt", 12, "/images/products/3.png", 2, 5),
        new(4, ".NET Foundation Sweatshirt", ".NET Foundation Sweatshirt", 12, "/images/products/4.png", 2, 2),
        new(5, "Roslyn Red Sheet", "Roslyn Red Sheet", 8.5M, "/images/products/5.png", 3, 5),
        new(6, ".NET Blue Sweatshirt", ".NET Blue Sweatshirt", 12, "/images/products/6.png", 2, 2),
        new(7, "Roslyn Red T-Shirt", "Roslyn Red T-Shirt", 12, "/images/products/7.png", 2, 5),
        new(8, "Kudu Purple Sweatshirt", "Kudu Purple Sweatshirt", 8.5M, "/images/products/8.png", 2, 5),
        new(9, "Cup<T> White Mug", "Cup<T> White Mug", 12, "/images/products/9.png", 1, 5),
        new(10, ".NET Foundation Sheet", ".NET Foundation Sheet", 12, "/images/products/10.png", 3, 2),
        new(11, "Cup<T> Sheet", "Cup<T> Sheet", 8.5M, "/images/products/11.png", 3, 2),
        new(12, "Prism White TShirt", "Prism White TShirt", 12, "/images/products/12.png", 2, 5),
    };

    /// <summary>Set to true to simulate the Catalog service being down.</summary>
    public bool Down { get; set; }

    private void ThrowIfDown()
    {
        if (Down) throw new Microsoft.eShopWeb.ApplicationCore.Exceptions.CatalogUnavailableException("Catalog service could not be reached.");
    }

    public Task<CatalogPageInfo> GetItemsAsync(int pageIndex, int pageSize, int? brandId, int? typeId, CancellationToken ct = default)
    {
        ThrowIfDown();
        var q = Items.Where(i => (!brandId.HasValue || i.CatalogBrandId == brandId) && (!typeId.HasValue || i.CatalogTypeId == typeId)).ToList();
        var page = pageSize > 0 ? q.Skip(pageIndex * pageSize).Take(pageSize).ToList() : q;
        return Task.FromResult(new CatalogPageInfo(page, q.Count));
    }

    public Task<CatalogItemInfo?> GetItemAsync(int id, CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
    }

    public Task<IReadOnlyList<CatalogItemInfo>> GetItemsByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        ThrowIfDown();
        var wanted = ids.ToHashSet();
        return Task.FromResult<IReadOnlyList<CatalogItemInfo>>(Items.Where(i => wanted.Contains(i.Id)).ToList());
    }

    public Task<IReadOnlyList<CatalogLookupInfo>> GetBrandsAsync(CancellationToken ct = default)
        => ThrowIfDownThen(() => Task.FromResult<IReadOnlyList<CatalogLookupInfo>>(new[] { "Azure", ".NET", "Visual Studio", "SQL Server", "Other" }
            .Select((n, i) => new CatalogLookupInfo(i + 1, n)).ToList()));

    public Task<IReadOnlyList<CatalogLookupInfo>> GetTypesAsync(CancellationToken ct = default)
        => ThrowIfDownThen(() => Task.FromResult<IReadOnlyList<CatalogLookupInfo>>(new[] { "Mug", "T-Shirt", "Sheet", "USB Memory Stick" }
            .Select((n, i) => new CatalogLookupInfo(i + 1, n)).ToList()));

    private Task<T> ThrowIfDownThen<T>(System.Func<Task<T>> f) { ThrowIfDown(); return f(); }
}
