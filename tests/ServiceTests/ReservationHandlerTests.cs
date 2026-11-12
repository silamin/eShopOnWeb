using Eshop.Messaging;
using InventoryService;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ServiceTests;

/// <summary>Inventory's side of the saga: all-or-nothing reservation, release on cancel, idempotency.</summary>
public class ReservationHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static async Task<InventoryDbContext> StockAsync(params (int Id, int Qty)[] items)
    {
        var db = Db.Inventory();
        foreach (var (id, qty) in items) db.StockItems.Add(StockItem.Create(id, $"Item {id}", qty, Now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static OrderCreated Order(int orderId, params (int Id, int Qty)[] lines) =>
        new(Guid.NewGuid(), Now, orderId, lines.Select(l => new OrderCreatedLine(l.Id, l.Qty)).ToList());

    private static async Task<int> QuantityAsync(InventoryDbContext db, int id) =>
        (await db.StockItems.AsNoTracking().SingleAsync(s => s.CatalogItemId == id)).QuantityInStore;

    [Fact]
    public async Task Enough_stock_reserves_and_answers_StockReserved()
    {
        var db = await StockAsync((1, 10), (2, 10));

        var (outcome, key, _) = await new ReservationHandler(db).HandleAsync(Order(1001, (1, 2), (2, 3)));

        Assert.IsType<StockReserved>(outcome);
        Assert.Equal(RoutingKeys.StockReserved, key);
        Assert.Equal(8, await QuantityAsync(db, 1));
        Assert.Equal(7, await QuantityAsync(db, 2));
    }

    [Fact]
    public async Task One_short_line_rejects_the_whole_order_and_touches_no_stock()
    {
        var db = await StockAsync((1, 10), (2, 10));

        var (outcome, key, _) = await new ReservationHandler(db).HandleAsync(Order(1002, (1, 2), (2, 11)));

        var rejected = Assert.IsType<StockRejected>(outcome);
        Assert.Equal(RoutingKeys.StockRejected, key);
        Assert.Contains("insufficient stock", rejected.Reason);
        Assert.Equal(10, await QuantityAsync(db, 1));   // all-or-nothing: item 1 was not reserved either
        Assert.Equal(10, await QuantityAsync(db, 2));
    }

    [Fact]
    public async Task Unknown_item_is_rejected()
    {
        var db = await StockAsync((1, 10));

        var (outcome, _, _) = await new ReservationHandler(db).HandleAsync(Order(1003, (42, 1)));

        Assert.Contains("No stock line", Assert.IsType<StockRejected>(outcome).Reason);
    }

    [Fact]
    public async Task Same_item_on_two_lines_counts_once_with_the_sum()
    {
        var db = await StockAsync((1, 5));

        var (outcome, _, _) = await new ReservationHandler(db).HandleAsync(Order(1004, (1, 3), (1, 3)));

        Assert.IsType<StockRejected>(outcome);           // 3 + 3 = 6 > 5
        Assert.Equal(5, await QuantityAsync(db, 1));
    }

    [Fact]
    public async Task Duplicate_OrderCreated_reserves_once()
    {
        var db = await StockAsync((1, 10));
        var evt = Order(1005, (1, 2));

        await new ReservationHandler(db).HandleAsync(evt);
        db.ChangeTracker.Clear();
        var (_, key, duplicate) = await new ReservationHandler(db).HandleAsync(evt);

        Assert.True(duplicate);
        Assert.Equal(RoutingKeys.StockReserved, key);
        Assert.Equal(8, await QuantityAsync(db, 1));
    }

    [Fact]
    public async Task OrderCancelled_releases_once()
    {
        var db = await StockAsync((1, 10));
        await new ReservationHandler(db).HandleAsync(Order(1006, (1, 4)));
        var cancel = new OrderCancelled(Guid.NewGuid(), Now, 1006, "PaymentFailed");

        db.ChangeTracker.Clear();
        await new ReservationHandler(db).HandleAsync(cancel);
        db.ChangeTracker.Clear();
        var second = await new ReservationHandler(db).HandleAsync(cancel);

        Assert.Equal(10, await QuantityAsync(db, 1));
        Assert.Contains("nothing to release", second);
    }

    [Fact]
    public async Task Cancelling_a_rejected_order_releases_nothing()
    {
        var db = await StockAsync((1, 1));
        await new ReservationHandler(db).HandleAsync(Order(1007, (1, 5)));

        db.ChangeTracker.Clear();
        var result = await new ReservationHandler(db).HandleAsync(new OrderCancelled(Guid.NewGuid(), Now, 1007, "StockRejected"));

        Assert.Contains("Rejected", result);
        Assert.Equal(1, await QuantityAsync(db, 1));
    }
}
