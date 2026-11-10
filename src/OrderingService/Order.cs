using Microsoft.EntityFrameworkCore;

namespace OrderingService;

public enum OrderStatus { Pending, StockReserved, Paid, Cancelled }

/// <summary>
/// The order as Ordering owns it: a snapshot of what was bought (BasketCheckedOut), never references
/// into Catalog. BuyerId is a plain string; no user data is copied in. CheckoutEventId (the EventId of the
/// BasketCheckedOut that created it) is unique, so a redelivered checkout cannot create a second order.
/// </summary>
public class Order
{
    public int Id { get; set; }
    public Guid CheckoutEventId { get; set; }
    public int BasketId { get; set; }
    public string BuyerId { get; set; } = "";
    public OrderStatus Status { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset OrderDate { get; set; }
    public List<OrderLine> Lines { get; set; } = new();

    public decimal Total => Lines.Sum(l => l.UnitPrice * l.Units);
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int CatalogItemId { get; set; }
    public string ProductName { get; set; } = "";
    public string PictureUri { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Units { get; set; }
}

public class OrderingDbContext : DbContext
{
    public OrderingDbContext(DbContextOptions<OrderingDbContext> options) : base(options) { }
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var o = b.Entity<Order>();
        o.ToTable("Orders");
        o.HasIndex(x => x.CheckoutEventId).IsUnique();
        o.Property(x => x.Status).HasConversion<string>().HasMaxLength(15);
        o.Property(x => x.BuyerId).HasMaxLength(256).IsRequired();
        o.Property(x => x.CancelReason).HasMaxLength(300);
        o.Ignore(x => x.Total);
        o.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);

        var l = b.Entity<OrderLine>();
        l.ToTable("OrderLines");
        l.Property(x => x.ProductName).HasMaxLength(50).IsRequired();
        l.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
    }
}
