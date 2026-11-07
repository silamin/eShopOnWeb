using Microsoft.EntityFrameworkCore;

namespace CatalogService;

public static class Seed
{
    // Same lists, same order as the monolith's CatalogContextSeed. Order matters: the HiLo
    // sequences hand out ids 1..n in insertion order, which is what keeps the ids identical.
    static readonly string[] Brands = { "Azure", ".NET", "Visual Studio", "SQL Server", "Other" };
    static readonly string[] Types = { "Mug", "T-Shirt", "Sheet", "USB Memory Stick" };

    // (typeId, brandId, name, price) - the monolith uses the name as description too.
    static readonly (int Type, int Brand, string Name, decimal Price)[] Items =
    {
        (2, 2, ".NET Bot Black Sweatshirt", 19.5M),
        (1, 2, ".NET Black & White Mug", 8.50M),
        (2, 5, "Prism White T-Shirt", 12),
        (2, 2, ".NET Foundation Sweatshirt", 12),
        (3, 5, "Roslyn Red Sheet", 8.5M),
        (2, 2, ".NET Blue Sweatshirt", 12),
        (2, 5, "Roslyn Red T-Shirt", 12),
        (2, 5, "Kudu Purple Sweatshirt", 8.5M),
        (1, 5, "Cup<T> White Mug", 12),
        (3, 2, ".NET Foundation Sheet", 12),
        (3, 2, "Cup<T> Sheet", 8.5M),
        (2, 5, "Prism White TShirt", 12),
    };

    public static async Task RunAsync(CatalogDbContext db, ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();

        if (!await db.CatalogBrands.AnyAsync())
        {
            db.CatalogBrands.AddRange(Brands.Select(b => new CatalogBrand { Brand = b }));
            await db.SaveChangesAsync();
        }
        if (!await db.CatalogTypes.AnyAsync())
        {
            db.CatalogTypes.AddRange(Types.Select(t => new CatalogType { Type = t }));
            await db.SaveChangesAsync();
        }
        if (!await db.CatalogItems.AnyAsync())
        {
            var n = 0;
            db.CatalogItems.AddRange(Items.Select(i => new CatalogItem
            {
                CatalogTypeId = i.Type,
                CatalogBrandId = i.Brand,
                Name = i.Name,
                Description = i.Name,
                Price = i.Price,
                PictureUri = $"http://catalogbaseurltobereplaced/images/products/{++n}.png",
            }));
            await db.SaveChangesAsync();

            // Guard for the "same ids" decision: item k must have id k.
            var ids = await db.CatalogItems.OrderBy(i => i.Id).Select(i => i.Id).ToListAsync();
            if (!ids.SequenceEqual(Enumerable.Range(1, Items.Length)))
                throw new InvalidOperationException("Seeded catalog item ids are not 1..N; ids no longer match the monolith.");
            logger.LogInformation("Seeded catalog-db: {Count} items with ids 1..{Count}", Items.Length, Items.Length);
        }
    }
}
