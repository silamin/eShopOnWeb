using Microsoft.EntityFrameworkCore;

namespace InventoryService;

/// <summary>
/// One stock line per catalog item. Ported from my own inventory-management-system (Dec 2024,
/// github.com/silamin/inventory-management-system): Entities/Item.cs (QuantityInStore, IsAvailable,
/// Item.Create) and the stock check in EfcOrderRepository.CreateOrderAsync ("insufficient stock.
/// Available: x, Required: y", then QuantityInStore -= quantity). What changed in the move:
/// the key is the catalog item id (Catalog owns the item; Inventory only owns the quantity), the
/// name is a copy taken from CatalogItemCreated, and the check lives on the entity, not the repository.
/// </summary>
public class StockItem
{
    public int CatalogItemId { get; private set; }
    public string Name { get; private set; } = "";
    public int QuantityInStore { get; private set; }
    public bool IsAvailable { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }

    private StockItem() { } // EF

    public static StockItem Create(int catalogItemId, string name, int startingQuantity, DateTimeOffset now)
    {
        if (catalogItemId < 1) throw new ArgumentOutOfRangeException(nameof(catalogItemId), "Catalog item id must be positive.");
        if (startingQuantity < 0) throw new ArgumentOutOfRangeException(nameof(startingQuantity), "QuantityInStore cannot be negative");
        return new StockItem { CatalogItemId = catalogItemId, Name = name, QuantityInStore = startingQuantity, CreatedAt = now };
    }

    /// <summary>Same rule as the old repository: enough stock or no change at all.</summary>
    public bool CanReserve(int quantity, out string reason)
    {
        if (quantity < 1) { reason = "Quantity must be at least 1."; return false; }
        if (!IsAvailable) { reason = $"Item '{Name}' is not available."; return false; }
        if (QuantityInStore < quantity)
        {
            reason = $"Item '{Name}' has insufficient stock. Available: {QuantityInStore}, Required: {quantity}";
            return false;
        }
        reason = "";
        return true;
    }

    public void Reserve(int quantity)
    {
        if (!CanReserve(quantity, out var reason)) throw new InvalidOperationException(reason);
        QuantityInStore -= quantity;
    }

    /// <summary>Compensation (OrderCancelled): puts a reserved quantity back.</summary>
    public void Release(int quantity)
    {
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        QuantityInStore += quantity;
    }
}

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<StockItem> StockItems => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var s = b.Entity<StockItem>();
        s.ToTable("StockItems");
        s.HasKey(x => x.CatalogItemId);
        // The catalog item id is Catalog's id, never generated here: a second CatalogItemCreated for the
        // same id hits the primary key, which is what makes the consumer idempotent.
        s.Property(x => x.CatalogItemId).ValueGeneratedNever();
        s.Property(x => x.Name).HasMaxLength(50).IsRequired();
        s.ToTable(t => t.HasCheckConstraint("CK_StockItems_QuantityInStore", "[QuantityInStore] >= 0"));
    }
}
