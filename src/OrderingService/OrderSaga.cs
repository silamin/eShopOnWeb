using Eshop.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace OrderingService;

/// <summary>
/// The order saga, orchestrated here (Ordering in charge). So far: checkout -> order Pending -> OrderCreated;
/// StockReserved -> order StockReserved (PaymentRequested is added with the Payment service);
/// StockRejected -> order Cancelled -> OrderCancelled. Every step checks the current status first, so a
/// redelivered event never moves an order twice.
/// </summary>
public sealed class OrderSaga
{
    private readonly OrderingDbContext _db;
    public OrderSaga(OrderingDbContext db) => _db = db;

    /// <summary>Creates the order (or finds the one this checkout already created) and returns OrderCreated to publish.</summary>
    public async Task<(OrderCreated Next, bool Duplicate)> HandleAsync(BasketCheckedOut evt)
    {
        var existing = await _db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.CheckoutEventId == evt.EventId);
        if (existing is not null) return (ToOrderCreated(existing), true);

        var order = new Order
        {
            CheckoutEventId = evt.EventId,
            BasketId = evt.BasketId,
            BuyerId = evt.BuyerId,
            Status = OrderStatus.Pending,
            OrderDate = DateTimeOffset.UtcNow,
            Lines = evt.Lines.Select(l => new OrderLine
            {
                CatalogItemId = l.CatalogItemId,
                ProductName = l.Name,
                PictureUri = l.PictureUri,
                UnitPrice = l.UnitPrice,
                Units = l.Quantity,
            }).ToList(),
        };
        _db.Orders.Add(order);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.CheckoutEventId == evt.EventId);
            return (ToOrderCreated(winner), true);
        }
        return (ToOrderCreated(order), false);
    }

    public async Task<string> HandleAsync(StockReserved evt)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == evt.OrderId);
        if (order is null) return "unknown order, ignored";
        if (order.Status != OrderStatus.Pending) return $"order is {order.Status}, no change";
        order.Status = OrderStatus.StockReserved;
        await _db.SaveChangesAsync();
        return "Pending -> StockReserved (PaymentRequested: with the Payment service)";
    }

    /// <summary>Returns the OrderCancelled to publish, or null when the order was already moved on.</summary>
    public async Task<(OrderCancelled? Next, string Result)> HandleAsync(StockRejected evt)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == evt.OrderId);
        if (order is null) return (null, "unknown order, ignored");
        if (order.Status == OrderStatus.Cancelled)
            return (new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, order.Id, order.CancelReason ?? ""), "already Cancelled, OrderCancelled re-published");
        if (order.Status != OrderStatus.Pending) return (null, $"order is {order.Status}, no change");
        order.Status = OrderStatus.Cancelled;
        order.CancelReason = $"StockRejected: {evt.Reason}";
        await _db.SaveChangesAsync();
        return (new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, order.Id, order.CancelReason), "Pending -> Cancelled");
    }

    private static OrderCreated ToOrderCreated(Order o) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, o.Id, o.Lines.Select(l => new OrderCreatedLine(l.CatalogItemId, l.Units)).ToList());
}
