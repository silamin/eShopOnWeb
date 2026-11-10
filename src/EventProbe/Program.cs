using System.Collections.Concurrent;
using Eshop.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// Messaging smoke test (run it with: docker compose run --rm event-probe).
// Declares the topology, starts one consumer per queue, publishes one event per routing key and checks that
// each message arrived ONLY in the queues whose binding pattern matches its key. Exit code 0 = routing works.
// The queues are durable and stay declared afterwards, so the bindings can be inspected in the management UI.

var options = RabbitMqOptions.FromEnvironment();
var received = new ConcurrentDictionary<string, ConcurrentBag<string>>(); // queue -> routing keys received

using var connection = await ConnectWhenReadyAsync(options);
using var channel = connection.CreateModel();
Topology.Declare(channel);
foreach (var queue in Topology.Bindings.Select(b => b.Queue).Distinct()) channel.QueuePurge(queue); // start from empty queues

foreach (var queue in Topology.Bindings.Select(b => b.Queue).Distinct())
{
    received[queue] = new ConcurrentBag<string>();
    var q = queue;
    var consumer = new EventingBasicConsumer(channel);
    consumer.Received += (_, ea) =>
    {
        received[q].Add($"{ea.RoutingKey} ({ea.BasicProperties.Type})");
        channel.BasicAck(ea.DeliveryTag, multiple: false);
    };
    channel.BasicConsume(q, autoAck: false, consumer);
}

using (var publisher = new EventPublisher(options))
{
    var now = DateTimeOffset.UtcNow;
    publisher.Publish(RoutingKeys.CatalogItemCreated, new CatalogItemCreated(Guid.NewGuid(), now, 13, "Probe item", 9.95m));
    publisher.Publish(RoutingKeys.BasketCheckedOut, new BasketCheckedOut(Guid.NewGuid(), now, 1, "probe-buyer",
        new[] { new BasketCheckedOutLine(13, "Probe item", "/images/products/13.png", 9.95m, 2) }));
    publisher.Publish(RoutingKeys.PaymentSucceeded, new PaymentSucceeded(Guid.NewGuid(), now, 42, 19.90m));
}

// Expected: key -> the queues that must receive it.
var expected = new Dictionary<string, string[]>
{
    [Queues.InventoryCatalogEvents] = new[] { RoutingKeys.CatalogItemCreated },
    [Queues.OrderingBasketEvents] = new[] { RoutingKeys.BasketCheckedOut },
    [Queues.OrderingPaymentEvents] = new[] { RoutingKeys.PaymentSucceeded },
};

var deadline = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < deadline && expected.Any(e => received[e.Key].Count < e.Value.Length)) await Task.Delay(100);
await Task.Delay(500); // grace period: catch a message that wrongly arrives in an extra queue

var ok = true;
foreach (var (queue, keys) in expected)
{
    var got = received[queue].Select(r => r.Split(' ')[0]).OrderBy(x => x).ToArray();
    var want = keys.OrderBy(x => x).ToArray();
    var pass = got.SequenceEqual(want);
    ok &= pass;
    Console.WriteLine($"{(pass ? "OK  " : "FAIL")} {queue,-26} bound '{Topology.Bindings.First(b => b.Queue == queue).Pattern}' received [{string.Join(", ", received[queue])}]");
}
Console.WriteLine(ok ? "Routing by key works: every event reached only the queue bound for it." : "Routing check FAILED.");
return ok ? 0 : 1;

static async Task<IConnection> ConnectWhenReadyAsync(RabbitMqOptions o)
{
    for (var attempt = 1; ; attempt++)
    {
        try { return o.ToConnectionFactory().CreateConnection("eshop-probe"); }
        catch (Exception ex) when (attempt < 30)
        {
            Console.WriteLine($"Broker not ready (attempt {attempt}/30): {ex.Message.Split('\n')[0]}");
            await Task.Delay(2000);
        }
    }
}
