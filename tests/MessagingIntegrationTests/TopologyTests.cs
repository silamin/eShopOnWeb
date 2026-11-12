using System.Collections.Concurrent;
using Eshop.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace MessagingIntegrationTests;

/// <summary>
/// Integration level (krav 5/6, test strategy): the shared topology and the publisher against a real
/// broker — routing by key, publisher confirms, and the dead-letter queue. No mocks.
/// </summary>
public class TopologyTests : IClassFixture<RabbitMqFixture>
{
    private readonly RabbitMqFixture _rabbit;
    public TopologyTests(RabbitMqFixture rabbit) => _rabbit = rabbit;

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>Drains a queue into a list of routing keys (waits until nothing new arrives for 1 s).</summary>
    private static async Task<List<string>> DrainAsync(IModel channel, string queue)
    {
        var got = new ConcurrentQueue<string>();
        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += (_, ea) => { got.Enqueue(ea.RoutingKey); channel.BasicAck(ea.DeliveryTag, false); };
        var tag = channel.BasicConsume(queue, autoAck: false, consumer);
        var last = -1;
        while (last != got.Count) { last = got.Count; await Task.Delay(1000); }
        channel.BasicCancel(tag);
        return got.ToList();
    }

    [Fact]
    public async Task Every_event_reaches_only_the_queues_bound_for_its_routing_key()
    {
        using (var publisher = new EventPublisher(_rabbit.Options))   // declares the topology, confirms each publish
        {
            publisher.Publish(RoutingKeys.CatalogItemCreated, new CatalogItemCreated(Guid.NewGuid(), Now, 13, "Probe", 9.95m));
            publisher.Publish(RoutingKeys.BasketCheckedOut, new BasketCheckedOut(Guid.NewGuid(), Now, 1, "buyer",
                new[] { new BasketCheckedOutLine(13, "Probe", "/p.png", 9.95m, 2) }));
            publisher.Publish(RoutingKeys.OrderCreated, new OrderCreated(Guid.NewGuid(), Now, 1, new[] { new OrderCreatedLine(13, 2) }));
            publisher.Publish(RoutingKeys.StockReserved, new StockReserved(Guid.NewGuid(), Now, 1));
            publisher.Publish(RoutingKeys.PaymentRequested, new PaymentRequested(Guid.NewGuid(), Now, 1, 19.90m));
            publisher.Publish(RoutingKeys.PaymentSucceeded, new PaymentSucceeded(Guid.NewGuid(), Now, 1, 19.90m));
            publisher.Publish(RoutingKeys.OrderCancelled, new OrderCancelled(Guid.NewGuid(), Now, 1, "test"));
        }

        using var connection = await BrokerConnect.OpenAsync(_rabbit.Options, "test-reader");
        using var channel = connection.CreateModel();

        Assert.Equal(new[] { "catalog.item.created" }, await DrainAsync(channel, Queues.InventoryCatalogEvents));
        Assert.Equal(new[] { "basket.checked-out" }, await DrainAsync(channel, Queues.OrderingBasketEvents));
        Assert.Equal(new[] { "order.created", "order.cancelled" }, await DrainAsync(channel, Queues.InventoryOrderEvents));
        Assert.Equal(new[] { "stock.reserved" }, await DrainAsync(channel, Queues.OrderingStockEvents));
        Assert.Equal(new[] { "order.payment-requested" }, await DrainAsync(channel, Queues.PaymentRequests));
        Assert.Equal(new[] { "payment.succeeded" }, await DrainAsync(channel, Queues.OrderingPaymentEvents)); // not payment-requested
    }

    [Fact]
    public async Task A_rejected_message_moves_to_the_dead_letter_queue_instead_of_being_lost()
    {
        using var connection = await BrokerConnect.OpenAsync(_rabbit.Options, "test-dlq");
        using var channel = connection.CreateModel();
        Topology.Declare(channel);
        channel.QueuePurge(Queues.PaymentRequests);
        channel.QueuePurge(Topology.DeadLetterQueue(Queues.PaymentRequests));

        channel.BasicPublish(Exchanges.Events, RoutingKeys.PaymentRequested, null, "not json"u8.ToArray());
        await Task.Delay(500);
        var poison = channel.BasicGet(Queues.PaymentRequests, autoAck: false);
        Assert.NotNull(poison);
        channel.BasicReject(poison.DeliveryTag, requeue: false);   // what a consumer does with an unreadable message
        await Task.Delay(500);

        var dead = channel.BasicGet(Topology.DeadLetterQueue(Queues.PaymentRequests), autoAck: true);
        Assert.NotNull(dead);
        Assert.Equal("not json", System.Text.Encoding.UTF8.GetString(dead.Body.Span));
        Assert.Equal(0u, channel.MessageCount(Queues.PaymentRequests));
    }

    [Fact]
    public async Task Event_round_trips_through_the_broker_with_its_contract_fields_and_correlation_id()
    {
        var sent = new PaymentFailed(Guid.NewGuid(), Now, 77, 117m, "Declined (simulated)");
        Correlation.Current = "checkout-42";   // what the HTTP request or the incoming message set
        using (var publisher = new EventPublisher(_rabbit.Options)) publisher.Publish(RoutingKeys.PaymentFailed, sent);
        Correlation.Current = null;

        using var connection = await BrokerConnect.OpenAsync(_rabbit.Options, "test-roundtrip");
        using var channel = connection.CreateModel();
        BasicGetResult? got = null;
        for (var i = 0; i < 20 && got is null; i++) { got = channel.BasicGet(Queues.OrderingPaymentEvents, autoAck: true); if (got is null) await Task.Delay(100); }

        Assert.NotNull(got);
        Assert.Equal(nameof(PaymentFailed), got!.BasicProperties.Type);
        Assert.Equal(sent.EventId.ToString(), got.BasicProperties.MessageId);
        Assert.True(got.BasicProperties.Persistent);
        Assert.Equal("checkout-42", got.BasicProperties.CorrelationId);   // the id travels with the event
        Assert.Equal(sent, EventPublisher.Deserialize<PaymentFailed>(got.Body));
    }
}
