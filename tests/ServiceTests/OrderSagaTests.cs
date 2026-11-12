using Eshop.Messaging;
using Microsoft.EntityFrameworkCore;
using OrderingService;
using Xunit;

namespace ServiceTests;

/// <summary>The order saga's state machine in Ordering: every step, both compensations, and idempotency.</summary>
public class OrderSagaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static BasketCheckedOut Checkout(params (int Id, decimal Price, int Qty)[] lines) =>
        new(Guid.NewGuid(), Now, 7, "demouser@microsoft.com",
            lines.Select(l => new BasketCheckedOutLine(l.Id, $"Item {l.Id}", $"/images/products/{l.Id}.png", l.Price, l.Qty)).ToList());

    private static async Task<(OrderingDbContext Db, OrderSaga Saga, int OrderId)> NewOrderAsync(params (int, decimal, int)[] lines)
    {
        var db = Db.Ordering();
        var saga = new OrderSaga(db);
        var (created, _) = await saga.HandleAsync(Checkout(lines.Length == 0 ? new[] { (1, 19.50m, 2) } : lines));
        return (db, saga, created.OrderId);
    }

    private static async Task<OrderStatus> StatusAsync(OrderingDbContext db, int id) =>
        (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id)).Status;

    [Fact]
    public async Task Checkout_creates_a_pending_order_and_OrderCreated_with_the_lines()
    {
        var db = Db.Ordering();
        var (created, duplicate) = await new OrderSaga(db).HandleAsync(Checkout((1, 19.50m, 2), (3, 12m, 1)));

        Assert.False(duplicate);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(51.00m, order.Total);
        Assert.Equal(order.Id, created.OrderId);
        Assert.Equal(new[] { (1, 2), (3, 1) }, created.Lines.Select(l => (l.CatalogItemId, l.Quantity)).ToArray());
    }

    [Fact]
    public async Task The_same_checkout_delivered_twice_creates_one_order()
    {
        var db = Db.Ordering();
        var saga = new OrderSaga(db);
        var evt = Checkout((1, 19.50m, 1));

        var first = await saga.HandleAsync(evt);
        var second = await saga.HandleAsync(evt);

        Assert.True(second.Duplicate);
        Assert.Equal(first.Next.OrderId, second.Next.OrderId);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Happy_path_Pending_StockReserved_Paid()
    {
        var (db, saga, id) = await NewOrderAsync((1, 19.50m, 2));

        var (payment, _) = await saga.HandleAsync(new StockReserved(Guid.NewGuid(), Now, id));
        Assert.NotNull(payment);
        Assert.Equal(39.00m, payment!.Amount);
        Assert.Equal(OrderStatus.StockReserved, await StatusAsync(db, id));

        await saga.HandleAsync(new PaymentSucceeded(Guid.NewGuid(), Now, id, 39.00m));
        Assert.Equal(OrderStatus.Paid, await StatusAsync(db, id));
    }

    [Fact]
    public async Task Rejected_stock_cancels_a_pending_order()
    {
        var (db, saga, id) = await NewOrderAsync();

        var (cancelled, _) = await saga.HandleAsync(new StockRejected(Guid.NewGuid(), Now, id, "insufficient stock"));

        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, await StatusAsync(db, id));
        Assert.Contains("insufficient stock", cancelled!.Reason);
    }

    [Fact]
    public async Task Failed_payment_cancels_and_asks_Inventory_to_release()
    {
        var (db, saga, id) = await NewOrderAsync((1, 117m, 1));
        await saga.HandleAsync(new StockReserved(Guid.NewGuid(), Now, id));

        var (cancelled, _) = await saga.HandleAsync(new PaymentFailed(Guid.NewGuid(), Now, id, 117m, "declined"));

        Assert.NotNull(cancelled);                  // OrderCancelled is what makes Inventory release the stock
        Assert.Equal(id, cancelled!.OrderId);
        Assert.Equal(OrderStatus.Cancelled, await StatusAsync(db, id));
    }

    [Fact]
    public async Task A_late_or_repeated_event_never_moves_a_paid_order()
    {
        var (db, saga, id) = await NewOrderAsync();
        await saga.HandleAsync(new StockReserved(Guid.NewGuid(), Now, id));
        await saga.HandleAsync(new PaymentSucceeded(Guid.NewGuid(), Now, id, 39m));

        await saga.HandleAsync(new PaymentSucceeded(Guid.NewGuid(), Now, id, 39m));
        var (afterFail, _) = await saga.HandleAsync(new PaymentFailed(Guid.NewGuid(), Now, id, 39m, "late"));
        var (afterReject, _) = await saga.HandleAsync(new StockRejected(Guid.NewGuid(), Now, id, "late"));

        Assert.Null(afterFail);
        Assert.Null(afterReject);
        Assert.Equal(OrderStatus.Paid, await StatusAsync(db, id));
    }

    [Fact]
    public async Task Payment_before_stock_is_reserved_is_ignored()
    {
        var (db, saga, id) = await NewOrderAsync();

        await saga.HandleAsync(new PaymentSucceeded(Guid.NewGuid(), Now, id, 39m));

        Assert.Equal(OrderStatus.Pending, await StatusAsync(db, id));
    }

    [Fact]
    public async Task Events_for_an_unknown_order_change_nothing()
    {
        var db = Db.Ordering();
        var saga = new OrderSaga(db);

        var (payment, result) = await saga.HandleAsync(new StockReserved(Guid.NewGuid(), Now, 999));

        Assert.Null(payment);
        Assert.Contains("unknown", result);
        Assert.Equal(0, await db.Orders.CountAsync());
    }
}
