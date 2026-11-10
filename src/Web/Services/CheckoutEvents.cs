using Eshop.Messaging;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.Web.Services;

/// <summary>The monolith's checkout tells Ordering about it: BasketCheckedOut on the topic exchange.</summary>
public interface ICheckoutEventPublisher
{
    Task PublishAsync(int basketId, CancellationToken ct = default);
}

/// <summary>
/// Explicit mapper: monolith domain types stay in the monolith, only the shared contract goes on the wire.
/// The line is a snapshot (id, name, picture, the basket's unit price, quantity), so Ordering never calls Catalog.
/// </summary>
public static class BasketCheckedOutMapper
{
    public static BasketCheckedOut Map(Basket basket, IReadOnlyList<CatalogItemInfo> catalogItems) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, basket.Id, basket.BuyerId,
            basket.Items.Select(i =>
            {
                var c = catalogItems.First(x => x.Id == i.CatalogItemId);
                return new BasketCheckedOutLine(c.Id, c.Name, c.PictureUri, i.UnitPrice, i.Quantity);
            }).ToList());
}

/// <summary>
/// One broker connection for the whole app, opened on first use. When no broker is configured
/// (RABBITMQ_USER missing, e.g. the functional tests or the monolith run on its own) it is disabled
/// and checkout says so in the log instead of failing.
/// </summary>
public sealed class BrokerConnection : IDisposable
{
    private readonly Lazy<EventPublisher?> _publisher;

    public BrokerConnection() =>
        _publisher = new Lazy<EventPublisher?>(() =>
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RABBITMQ_USER"))
                ? null
                : new EventPublisher(RabbitMqOptions.FromEnvironment()));

    public EventPublisher? Publisher => _publisher.Value;

    public void Dispose()
    {
        if (_publisher.IsValueCreated) _publisher.Value?.Dispose();
    }
}

public sealed class CheckoutEventPublisher : ICheckoutEventPublisher
{
    private readonly IRepository<Basket> _baskets;
    private readonly ICatalogClient _catalog;
    private readonly BrokerConnection _broker;
    private readonly ILogger<CheckoutEventPublisher> _log;

    public CheckoutEventPublisher(IRepository<Basket> baskets, ICatalogClient catalog, BrokerConnection broker,
        ILogger<CheckoutEventPublisher> log)
    {
        _baskets = baskets;
        _catalog = catalog;
        _broker = broker;
        _log = log;
    }

    public async Task PublishAsync(int basketId, CancellationToken ct = default)
    {
        var publisher = _broker.Publisher;
        if (publisher is null)
        {
            _log.LogWarning("No broker configured: BasketCheckedOut for basket {BasketId} not published", basketId);
            return;
        }
        var basket = await _baskets.FirstOrDefaultAsync(new BasketWithItemsSpecification(basketId), ct)
            ?? throw new InvalidOperationException($"Basket {basketId} not found at checkout.");
        var items = await _catalog.GetItemsByIdsAsync(basket.Items.Select(i => i.CatalogItemId), ct);
        var evt = BasketCheckedOutMapper.Map(basket, items);
        publisher.Publish(RoutingKeys.BasketCheckedOut, evt); // throws if the broker does not confirm
        _log.LogInformation("BasketCheckedOut published for basket {BasketId} ({Lines} lines, event {EventId})",
            basketId, evt.Lines.Count, evt.EventId);
    }
}
