using RabbitMQ.Client;

namespace Eshop.Messaging;

/// <summary>
/// One correlation id follows a customer action across every service (krav 7, logging/monitoring):
/// the gateway sets X-Correlation-Id on the HTTP request (or keeps the caller's), Web and Catalog read it,
/// EventPublisher puts it on every message (AMQP correlation_id), and each consumer takes it from the
/// message before handling it — so the events it publishes next carry the same id. Every service logs it
/// as the property CorrelationId: one filter in Seq shows the whole checkout → order → stock → payment chain.
/// </summary>
public static class Correlation
{
    public const string Header = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";

    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The id of the work being done on this async flow, or null outside a request/message.</summary>
    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Takes the id from an incoming message (a new one if it has none) and makes it Current.</summary>
    public static string FromMessage(IBasicProperties? props) =>
        Current = string.IsNullOrEmpty(props?.CorrelationId) ? NewId() : props!.CorrelationId;
}
