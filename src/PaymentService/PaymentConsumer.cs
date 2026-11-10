using Eshop.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PaymentService;

/// <summary>
/// Consumes payment.requests (order.payment-requested). Decide, commit, publish PaymentSucceeded /
/// PaymentFailed with publisher confirms, then ack. A duplicate re-publishes the stored decision.
/// </summary>
public sealed class PaymentConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PaymentConsumer> _log;
    private readonly EventPublisher _publisher;
    private IConnection? _connection;
    private IModel? _channel;

    public PaymentConsumer(IServiceScopeFactory scopes, ILogger<PaymentConsumer> log, EventPublisher publisher)
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
            try { _connection = options.ToConnectionFactory().CreateConnection("payment-service"); }
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
        _channel.BasicConsume(Queues.PaymentRequests, autoAck: false, consumer);
        using (var scope = _scopes.CreateScope())
            _log.LogInformation("Consuming {Queue}; declines amounts above {Limit}", Queues.PaymentRequests,
                scope.ServiceProvider.GetRequiredService<PaymentDecider>().DeclineAbove);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    private void Handle(BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        try
        {
            if (ea.RoutingKey != RoutingKeys.PaymentRequested)
            {
                _log.LogInformation("Ignored {RoutingKey}", ea.RoutingKey);
                channel.BasicAck(ea.DeliveryTag, false);
                return;
            }
            PaymentRequested? evt;
            try { evt = EventPublisher.Deserialize<PaymentRequested>(ea.Body); }
            catch (System.Text.Json.JsonException) { evt = null; }
            if (evt is null || evt.OrderId < 1)
            {
                _log.LogError("Unreadable PaymentRequested (message id {MessageId}); rejected to the dead-letter queue", ea.BasicProperties.MessageId);
                channel.BasicReject(ea.DeliveryTag, requeue: false);
                return;
            }
            using var scope = _scopes.CreateScope();
            var (outcome, key, duplicate) = scope.ServiceProvider.GetRequiredService<PaymentDecider>().HandleAsync(evt).GetAwaiter().GetResult();
            if (outcome is PaymentSucceeded s) _publisher.Publish(key, s);
            else _publisher.Publish(key, (PaymentFailed)outcome);
            _log.LogInformation("PaymentRequested order {OrderId} amount {Amount}: {Key}{Dup}", evt.OrderId, evt.Amount, key,
                duplicate ? " (duplicate: stored decision re-published)" : "");
            channel.BasicAck(ea.DeliveryTag, false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handling {RoutingKey} failed; requeued in 2 s", ea.RoutingKey);
            Thread.Sleep(2000);
            channel.BasicNack(ea.DeliveryTag, false, requeue: true);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
