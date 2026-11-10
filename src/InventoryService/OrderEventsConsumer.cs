using Eshop.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InventoryService;

/// <summary>
/// Consumes inventory.order-events (bound to order.*). OrderCreated: reserve or reject, commit, publish
/// StockReserved / StockRejected with publisher confirms, and only then ack the incoming message. A crash
/// anywhere before the ack means a redelivery, which re-publishes the stored outcome (never a second
/// reservation). OrderCancelled: release a Reserved reservation. Manual ack, one message at a time.
/// </summary>
public sealed class OrderEventsConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OrderEventsConsumer> _log;
    private readonly EventPublisher _publisher;
    private IConnection? _connection;
    private IModel? _channel;

    public OrderEventsConsumer(IServiceScopeFactory scopes, ILogger<OrderEventsConsumer> log, EventPublisher publisher)
    {
        _scopes = scopes;
        _log = log;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = RabbitMqOptions.FromEnvironment();
        for (var attempt = 1; _connection is null; attempt++)
        {
            try { _connection = options.ToConnectionFactory().CreateConnection("inventory-orders"); }
            catch (Exception ex) when (attempt < 30 && !stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning("Broker not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(2000, stoppingToken);
            }
        }
        _channel = _connection.CreateModel();
        Topology.Declare(_channel);
        _channel.BasicQos(0, 1, false);
        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, ea) => Handle(ea);
        _channel.BasicConsume(Queues.InventoryOrderEvents, autoAck: false, consumer);
        _log.LogInformation("Consuming {Queue}", Queues.InventoryOrderEvents);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    private void Handle(BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        try
        {
            switch (ea.RoutingKey)
            {
                case RoutingKeys.OrderCreated:
                {
                    var evt = Read<OrderCreated>(ea);
                    if (evt is null || evt.OrderId < 1) { Reject(ea, "OrderCreated"); return; }
                    using var scope = _scopes.CreateScope();
                    var (outcome, key, duplicate) = scope.ServiceProvider.GetRequiredService<ReservationHandler>()
                        .HandleAsync(evt).GetAwaiter().GetResult();
                    if (outcome is StockReserved r) _publisher.Publish(key, r);
                    else _publisher.Publish(key, (StockRejected)outcome);   // throws if the broker does not confirm
                    _log.LogInformation("OrderCreated {OrderId}: {Key}{Dup} {Reason}", evt.OrderId, key,
                        duplicate ? " (duplicate: stored outcome re-published)" : "",
                        outcome is StockRejected rej ? "- " + rej.Reason : "");
                    break;
                }
                case RoutingKeys.OrderCancelled:
                {
                    var evt = Read<OrderCancelled>(ea);
                    if (evt is null || evt.OrderId < 1) { Reject(ea, "OrderCancelled"); return; }
                    using var scope = _scopes.CreateScope();
                    var result = scope.ServiceProvider.GetRequiredService<ReservationHandler>()
                        .HandleAsync(evt).GetAwaiter().GetResult();
                    _log.LogInformation("OrderCancelled {OrderId}: {Result}", evt.OrderId, result);
                    break;
                }
                default:
                    _log.LogInformation("Ignored {RoutingKey}", ea.RoutingKey);
                    break;
            }
            channel.BasicAck(ea.DeliveryTag, false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handling {RoutingKey} failed; requeued in 2 s", ea.RoutingKey);
            Thread.Sleep(2000);
            channel.BasicNack(ea.DeliveryTag, false, requeue: true);
        }
    }

    private static T? Read<T>(BasicDeliverEventArgs ea) where T : class
    {
        try { return EventPublisher.Deserialize<T>(ea.Body); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private void Reject(BasicDeliverEventArgs ea, string what)
    {
        _log.LogError("Unreadable {What} (message id {MessageId}); rejected to the dead-letter queue", what, ea.BasicProperties.MessageId);
        _channel!.BasicReject(ea.DeliveryTag, requeue: false);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
