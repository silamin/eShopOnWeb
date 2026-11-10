using Eshop.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderingService;

/// <summary>
/// Consumes ordering.basket-events (basket.checked-out) and ordering.stock-events (stock.*). Commit, then
/// publish with publisher confirms, then ack. Manual ack, one message at a time per queue. An unreadable
/// message is rejected (not requeued); a failure while handling is requeued after 2 s.
/// </summary>
public sealed class SagaConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SagaConsumer> _log;
    private readonly EventPublisher _publisher;
    private IConnection? _connection;
    private IModel? _channel;

    public SagaConsumer(IServiceScopeFactory scopes, ILogger<SagaConsumer> log, EventPublisher publisher)
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
            try { _connection = options.ToConnectionFactory().CreateConnection("ordering-saga"); }
            catch (Exception ex) when (attempt < 30 && !stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning("Broker not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(2000, stoppingToken);
            }
        }
        _channel = _connection.CreateModel();
        Topology.Declare(_channel);
        _channel.BasicQos(0, 1, false);
        foreach (var queue in new[] { Queues.OrderingBasketEvents, Queues.OrderingStockEvents })
        {
            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += (_, ea) => Handle(ea);
            _channel.BasicConsume(queue, autoAck: false, consumer);
            _log.LogInformation("Consuming {Queue}", queue);
        }
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    private void Handle(BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        try
        {
            using var scope = _scopes.CreateScope();
            var saga = scope.ServiceProvider.GetRequiredService<OrderSaga>();
            switch (ea.RoutingKey)
            {
                case RoutingKeys.BasketCheckedOut:
                {
                    var evt = Read<BasketCheckedOut>(ea);
                    if (evt is null || evt.Lines is null || evt.Lines.Count == 0 || string.IsNullOrEmpty(evt.BuyerId)) { Reject(ea); return; }
                    var (next, dup) = saga.HandleAsync(evt).GetAwaiter().GetResult();
                    _publisher.Publish(RoutingKeys.OrderCreated, next);
                    _log.LogInformation("BasketCheckedOut basket {BasketId}: order {OrderId}{Dup}, OrderCreated published",
                        evt.BasketId, next.OrderId, dup ? " (duplicate checkout: existing order)" : " created, Pending");
                    break;
                }
                case RoutingKeys.StockReserved:
                {
                    var evt = Read<StockReserved>(ea);
                    if (evt is null) { Reject(ea); return; }
                    var result = saga.HandleAsync(evt).GetAwaiter().GetResult();
                    _log.LogInformation("StockReserved order {OrderId}: {Result}", evt.OrderId, result);
                    break;
                }
                case RoutingKeys.StockRejected:
                {
                    var evt = Read<StockRejected>(ea);
                    if (evt is null) { Reject(ea); return; }
                    var (next, result) = saga.HandleAsync(evt).GetAwaiter().GetResult();
                    if (next is not null) _publisher.Publish(RoutingKeys.OrderCancelled, next);
                    _log.LogInformation("StockRejected order {OrderId}: {Result}", evt.OrderId, result);
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

    private void Reject(BasicDeliverEventArgs ea)
    {
        _log.LogError("Unreadable {RoutingKey} (message id {MessageId}); rejected", ea.RoutingKey, ea.BasicProperties.MessageId);
        _channel!.BasicReject(ea.DeliveryTag, requeue: false);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
