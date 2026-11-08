namespace Eshop.Messaging;

// Shared event contracts. These are the only shapes that travel on the wire: services map their own
// domain types to and from these explicitly (no domain types on the message). Every event carries an
// EventId (identifies this message, used for idempotency/trace) and OccurredAt (UTC).
// Only the events needed so far are here; OrderCreated, StockReserved/Rejected, PaymentRequested,
// PaymentFailed and OrderCancelled are added with the service that publishes them.

public interface IEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Catalog to Inventory: a new catalog item exists, so Inventory creates a stock line.</summary>
public sealed record CatalogItemCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int CatalogItemId,
    string Name,
    decimal Price) : IEvent;

/// <summary>Monolith to Ordering: a basket was checked out. Item snapshot, so Ordering never calls Catalog.</summary>
public sealed record BasketCheckedOut(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int BasketId,
    string BuyerId,
    IReadOnlyList<BasketCheckedOutLine> Lines) : IEvent;

public sealed record BasketCheckedOutLine(
    int CatalogItemId,
    string Name,
    string PictureUri,
    decimal UnitPrice,
    int Quantity);

/// <summary>Payment to Ordering: the (simulated) payment for the order went through.</summary>
public sealed record PaymentSucceeded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int OrderId,
    decimal Amount) : IEvent;
