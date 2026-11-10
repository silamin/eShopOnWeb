namespace InventoryService;

public enum ReservationStatus { Reserved, Rejected, Released }

/// <summary>
/// The stored decision for one order. OrderId is the primary key, so the same OrderCreated handled
/// twice cannot reserve twice. Rejected is stored too, so a redelivery repeats the same StockRejected
/// and does not re-check stock that may have changed since. Only a Reserved row is ever released.
/// </summary>
public class Reservation
{
    public int OrderId { get; set; }
    public ReservationStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public List<ReservationLine> Lines { get; set; } = new();
}

public class ReservationLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int CatalogItemId { get; set; }
    public int Quantity { get; set; }
}
