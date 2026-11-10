using RabbitMQ.Client;

namespace Eshop.Messaging;

/// <summary>The one topic exchange all services publish to.</summary>
public static class Exchanges
{
    public const string Events = "eshop.events";
}

/// <summary>Routing keys: dot-separated, lower case. Publishers choose the key; consumers bind by pattern.</summary>
public static class RoutingKeys
{
    public const string CatalogItemCreated = "catalog.item.created";
    public const string BasketCheckedOut = "basket.checked-out";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string OrderCreated = "order.created";
    public const string OrderCancelled = "order.cancelled";
    public const string StockReserved = "stock.reserved";
    public const string StockRejected = "stock.rejected";
    // Not "payment.requested": that would match Ordering's own "payment.*" binding.
    public const string PaymentRequested = "order.payment-requested";
    public const string PaymentFailed = "payment.failed";
}

/// <summary>One durable queue per consumer and concern. A queue only gets the keys it is bound for.</summary>
public static class Queues
{
    public const string InventoryCatalogEvents = "inventory.catalog-events";
    public const string OrderingBasketEvents = "ordering.basket-events";
    public const string OrderingPaymentEvents = "ordering.payment-events";
    public const string InventoryOrderEvents = "inventory.order-events";
    public const string OrderingStockEvents = "ordering.stock-events";
    public const string PaymentRequests = "payment.requests";
}

public static class Topology
{
    /// <summary>Queue and the binding pattern that feeds it. '*' = exactly one word, '#' = zero or more words.</summary>
    public static readonly IReadOnlyList<(string Queue, string Pattern)> Bindings = new[]
    {
        (Queues.InventoryCatalogEvents, "catalog.item.*"),
        (Queues.OrderingBasketEvents, RoutingKeys.BasketCheckedOut),
        (Queues.OrderingPaymentEvents, "payment.*"),
        (Queues.InventoryOrderEvents, RoutingKeys.OrderCreated),     // explicit keys, not order.*:
        (Queues.InventoryOrderEvents, RoutingKeys.OrderCancelled),   // order.payment-requested is not Inventory's
        (Queues.OrderingStockEvents, "stock.*"),
        (Queues.PaymentRequests, RoutingKeys.PaymentRequested),
    };

    /// <summary>Idempotent: declaring what already exists with the same arguments changes nothing.</summary>
    public static void Declare(IModel channel)
    {
        channel.ExchangeDeclare(Exchanges.Events, ExchangeType.Topic, durable: true, autoDelete: false);
        foreach (var (queue, pattern) in Bindings)
        {
            channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
            channel.QueueBind(queue, Exchanges.Events, pattern);
        }
    }
}
