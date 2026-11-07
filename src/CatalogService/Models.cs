using Microsoft.EntityFrameworkCore;

namespace CatalogService;

// The Catalog service owns its own model. It does not reference the monolith's ApplicationCore.
// Ids use the same HiLo sequences as the monolith, so a fresh catalog-db assigns the same ids
// to the same seed rows (strangler data-migration step: same ids).

public class CatalogBrand
{
    public int Id { get; set; }
    public string Brand { get; set; } = "";
}

public class CatalogType
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
}

public class CatalogItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string? PictureUri { get; set; }
    public int CatalogTypeId { get; set; }
    public int CatalogBrandId { get; set; }
}

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<CatalogBrand> CatalogBrands => Set<CatalogBrand>();
    public DbSet<CatalogType> CatalogTypes => Set<CatalogType>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CatalogItem>(e =>
        {
            e.ToTable("Catalog");
            e.Property(x => x.Id).UseHiLo("catalog_hilo").IsRequired();
            e.Property(x => x.Name).IsRequired().HasMaxLength(50);
            e.Property(x => x.Price).IsRequired().HasColumnType("decimal(18,2)");
            e.Property(x => x.PictureUri).IsRequired(false);
            e.HasOne<CatalogBrand>().WithMany().HasForeignKey(x => x.CatalogBrandId);
            e.HasOne<CatalogType>().WithMany().HasForeignKey(x => x.CatalogTypeId);
        });
        b.Entity<CatalogBrand>(e =>
        {
            e.ToTable("CatalogBrands");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseHiLo("catalog_brand_hilo").IsRequired();
            e.Property(x => x.Brand).IsRequired().HasMaxLength(100);
        });
        b.Entity<CatalogType>(e =>
        {
            e.ToTable("CatalogTypes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseHiLo("catalog_type_hilo").IsRequired();
            e.Property(x => x.Type).IsRequired().HasMaxLength(100);
        });
    }
}

// JSON shapes identical to the Public API's catalog endpoints (the Blazor admin depends on them).
public class CatalogItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string PictureUri { get; set; } = "";
    public int CatalogTypeId { get; set; }
    public int CatalogBrandId { get; set; }
}

public record CatalogBrandDto(int Id, string Name);
public record CatalogTypeDto(int Id, string Name);

public class CreateCatalogItemRequest
{
    public int CatalogBrandId { get; set; }
    public int CatalogTypeId { get; set; }
    public string Description { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PictureUri { get; set; }
    public string? PictureBase64 { get; set; }
    public string? PictureName { get; set; }
    public decimal Price { get; set; }
}

public class UpdateCatalogItemRequest
{
    public int Id { get; set; }
    public int CatalogBrandId { get; set; }
    public int CatalogTypeId { get; set; }
    public string Description { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PictureBase64 { get; set; }
    public string? PictureUri { get; set; }
    public string? PictureName { get; set; }
    public decimal Price { get; set; }
}

public class DuplicateException : Exception
{
    public DuplicateException(string message) : base(message) { }
}
