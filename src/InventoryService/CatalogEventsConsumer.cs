using Eshop.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InventoryService;

/// <summary>
/// Consumes the queue inventory.catalog-events (bound to catalog.item.*). CatalogItemCreated creates a
/// stock line with INVENTORY_STARTING_QUANTITY. Manual ack, one message at a time, ack only after the
/// database commit: a crash before the commit means a redelivery, not a lost stock line.
/// Idempotent: a redelivered or duplicate event for an id that already has a stock line changes nothing.
/// A message that cannot be read is rejected without requeue: it moves to inventory.catalog-events.dlq.
/// </summary>
public sealed class CatalogEventsConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CatalogEventsConsumer> _log;
    private readonly int _startingQuantity;
    private IConnection? _connection;
    private IModel? _channel;

    public CatalogEventsConsumer(IServiceScopeFactory scopes, ILogger<CatalogEventsConsumer> log, IConfiguration config)
    {
        _scopes = scopes;
        _log = log;
        _startingQuantity = int.TryParse(config["INVENTORY_STARTING_QUANTITY"], out var q) && q >= 0 ? q : 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = RabbitMqOptions.FromEnvironment();
        _connection = await BrokerConnect.OpenAsync(options, "inventory-service", m => _log.LogWarning("{Retry}", m), stoppingToken);

        _channel = _connection.CreateModel();
        Topology.Declare(_channel);
        _channel.BasicQos(0, 1, false);

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, ea) =>
        {
            // The message's correlation id becomes this handler's: on every log line, and on what it publishes.
            using (_log.BeginScope(new Dictionary<string, object> { [Correlation.LogProperty] = Correlation.FromMessage(ea.BasicProperties) }))
                Handle(ea);
        };
        _channel.BasicConsume(Queues.InventoryCatalogEvents, autoAck: false, consumer);
        _log.LogInformation("Consuming {Queue}; starting quantity for new stock lines = {Quantity}",
            Queues.InventoryCatalogEvents, _startingQuantity);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    private void Handle(BasicDeliverEventArgs ea)
    {
        var channel = _channel!;
        try
        {
            if (ea.RoutingKey != RoutingKeys.CatalogItemCreated)
            {
                // Bound to catalog.item.*: updated/deleted are not handled yet. Ack so they do not pile up.
                _log.LogInformation("Ignored {RoutingKey}", ea.RoutingKey);
                channel.BasicAck(ea.DeliveryTag, false);
                return;
            }

            CatalogItemCreated? evt;
            try { evt = EventPublisher.Deserialize<CatalogItemCreated>(ea.Body); }
            catch (System.Text.Json.JsonException) { evt = null; }
            if (evt is null || evt.CatalogItemId < 1)
            {
                _log.LogError("Unreadable CatalogItemCreated (message id {MessageId}); rejected to the dead-letter queue", ea.BasicProperties.MessageId);
                channel.BasicReject(ea.DeliveryTag, requeue: false);
                return;
            }

            var outcome = CreateStockLineAsync(evt).GetAwaiter().GetResult();
            _log.LogInformation("CatalogItemCreated {CatalogItemId} '{Name}' (event {EventId}): {Outcome}",
                evt.CatalogItemId, evt.Name, evt.EventId, outcome);
            channel.BasicAck(ea.DeliveryTag, false);
        }
        catch (Exception ex)
        {
            // Database down or similar: put it back and try again, but not in a tight loop.
            // (An unreadable message never gets here: it is rejected above, so it cannot loop forever.)
            _log.LogError(ex, "Handling {RoutingKey} failed; requeued in 2 s", ea.RoutingKey);
            Thread.Sleep(2000);
            channel.BasicNack(ea.DeliveryTag, false, requeue: true);
        }
    }

    private async Task<string> CreateStockLineAsync(CatalogItemCreated evt)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        if (await db.StockItems.AsNoTracking().AnyAsync(s => s.CatalogItemId == evt.CatalogItemId))
            return "stock line already exists (duplicate, no change)";

        db.StockItems.Add(StockItem.Create(evt.CatalogItemId, evt.Name, _startingQuantity, DateTimeOffset.UtcNow));
        try
        {
            await db.SaveChangesAsync();
            return $"stock line created, quantity {_startingQuantity}";
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            return "stock line already exists (concurrent duplicate, no change)";
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
