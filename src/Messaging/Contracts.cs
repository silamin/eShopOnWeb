namespace Eshop.Messaging;

// Shared event contracts. These are the only shapes that travel on the wire: services map their own
// domain types to and from these explicitly (no domain types on the message). Every event carries an
// EventId (identifies this message, used for idempotency/trace) and OccurredAt (UTC).
// Only the events needed so far are here; PaymentRequested and PaymentFailed are added with the service
// that publishes them.

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

/// <summary>Ordering to Inventory: an order exists (pending); Inventory reserves its lines or rejects the order.</summary>
public sealed record OrderCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int OrderId,
    IReadOnlyList<OrderCreatedLine> Lines) : IEvent;

public sealed record OrderCreatedLine(int CatalogItemId, int Quantity);

/// <summary>Ordering to Inventory (compensation): the order is cancelled; Inventory releases a Reserved reservation.</summary>
public sealed record OrderCancelled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int OrderId,
    string Reason) : IEvent;

/// <summary>Inventory to Ordering: every line of the order is reserved.</summary>
public sealed record StockReserved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int OrderId) : IEvent;

/// <summary>Inventory to Ordering: at least one line could not be reserved; nothing was reserved.</summary>
public sealed record StockRejected(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int OrderId,
    string Reason) : IEvent;
