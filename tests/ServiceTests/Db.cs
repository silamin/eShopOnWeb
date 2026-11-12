using InventoryService;
using Microsoft.EntityFrameworkCore;
using OrderingService;
using PaymentService;

namespace ServiceTests;

/// <summary>Each test gets its own EF Core in-memory database (unit level: logic, not SQL).</summary>
internal static class Db
{
    private static DbContextOptions<T> Options<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    public static InventoryDbContext Inventory() => new(Options<InventoryDbContext>());
    public static OrderingDbContext Ordering() => new(Options<OrderingDbContext>());
    public static PaymentDbContext Payment() => new(Options<PaymentDbContext>());
}
