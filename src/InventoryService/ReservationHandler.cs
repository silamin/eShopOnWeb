using Eshop.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace InventoryService;

/// <summary>
/// Reservation at checkout (OrderCreated) and release on cancel (OrderCancelled). All or nothing:
/// either every line of the order is reserved, or nothing changes and the order is rejected. The stock
/// change and the reservation row are written by one SaveChanges, i.e. one database transaction.
/// </summary>
public sealed class ReservationHandler
{
    private readonly InventoryDbContext _db;
    public ReservationHandler(InventoryDbContext db) => _db = db;

    /// <summary>Returns the event to publish: StockReserved or StockRejected (the stored one on a duplicate).</summary>
    public async Task<(IEvent Outcome, string RoutingKey, bool Duplicate)> HandleAsync(OrderCreated evt)
    {
        var stored = await _db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == evt.OrderId);
        if (stored is not null) return (OutcomeOf(stored), KeyOf(stored), true);

        var reservation = new Reservation { OrderId = evt.OrderId, CreatedAt = DateTimeOffset.UtcNow };
        var reason = await TryReserveAsync(evt, reservation);
        if (reason is null)
        {
            reservation.Status = ReservationStatus.Reserved;
        }
        else
        {
            reservation.Status = ReservationStatus.Rejected;
            reservation.Reason = reason;
            reservation.Lines.Clear();
            foreach (var e in _db.ChangeTracker.Entries<StockItem>()) e.State = EntityState.Unchanged; // undo in-memory reserves
        }
        _db.Reservations.Add(reservation);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // Another delivery of the same order committed first: answer with what it stored.
            _db.ChangeTracker.Clear();
            var winner = await _db.Reservations.AsNoTracking().FirstAsync(r => r.OrderId == evt.OrderId);
            return (OutcomeOf(winner), KeyOf(winner), true);
        }
        // DbUpdateConcurrencyException (stock changed under us) is not caught: the consumer requeues and it runs again.

        return (OutcomeOf(reservation), KeyOf(reservation), false);
    }

    /// <summary>Null when every line is reserved (in memory, not yet saved), otherwise why not.</summary>
    private async Task<string?> TryReserveAsync(OrderCreated evt, Reservation reservation)
    {
        if (evt.Lines is null || evt.Lines.Count == 0) return "Order has no lines.";
        if (evt.Lines.Any(l => l.Quantity < 1)) return "Every line needs a quantity of at least 1.";

        // The same item on two lines counts once, with the summed quantity.
        var wanted = evt.Lines.GroupBy(l => l.CatalogItemId).Select(g => (Id: g.Key, Qty: g.Sum(l => l.Quantity))).ToList();
        var ids = wanted.Select(w => w.Id).ToArray();
        var stock = await _db.StockItems.Where(s => ids.Contains(s.CatalogItemId)).ToDictionaryAsync(s => s.CatalogItemId);

        foreach (var (id, qty) in wanted)
        {
            if (!stock.TryGetValue(id, out var item)) return $"No stock line for catalog item {id}.";
            if (!item.CanReserve(qty, out var why)) return why;
        }
        foreach (var (id, qty) in wanted)
        {
            stock[id].Reserve(qty);
            reservation.Lines.Add(new ReservationLine { OrderId = evt.OrderId, CatalogItemId = id, Quantity = qty });
        }
        return null;
    }

    /// <summary>Compensation. Releases only a Reserved row; a repeat, a Rejected row or an unknown order changes nothing.</summary>
    public async Task<string> HandleAsync(OrderCancelled evt)
    {
        var r = await _db.Reservations.Include(x => x.Lines).FirstOrDefaultAsync(x => x.OrderId == evt.OrderId);
        if (r is null) return "no reservation for this order, nothing to release";
        if (r.Status != ReservationStatus.Reserved) return $"reservation is {r.Status}, nothing to release";

        var ids = r.Lines.Select(l => l.CatalogItemId).ToArray();
        var stock = await _db.StockItems.Where(s => ids.Contains(s.CatalogItemId)).ToDictionaryAsync(s => s.CatalogItemId);
        foreach (var line in r.Lines) stock[line.CatalogItemId].Release(line.Quantity);
        r.Status = ReservationStatus.Released;
        r.ReleasedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return $"released {r.Lines.Sum(l => l.Quantity)} unit(s) on {r.Lines.Count} line(s)";
    }

    private static string KeyOf(Reservation r) =>
        r.Status == ReservationStatus.Rejected ? RoutingKeys.StockRejected : RoutingKeys.StockReserved;

    // A Released reservation was Reserved when it was decided, so its outcome is still StockReserved.
    private static IEvent OutcomeOf(Reservation r) => r.Status == ReservationStatus.Rejected
        ? new StockRejected(Guid.NewGuid(), DateTimeOffset.UtcNow, r.OrderId, r.Reason ?? "")
        : new StockReserved(Guid.NewGuid(), DateTimeOffset.UtcNow, r.OrderId);
}
