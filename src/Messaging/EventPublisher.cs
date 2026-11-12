using System.Text.Json;
using RabbitMQ.Client;

namespace Eshop.Messaging;

/// <summary>
/// Publishes events to the topic exchange with a routing key. Persistent messages and publisher confirms:
/// Publish returns only after the broker has confirmed the message, or throws.
/// </summary>
public sealed class EventPublisher : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly object _gate = new();

    /// <param name="log">Optional: where "broker not ready" retries are written (a service's ILogger).</param>
    public EventPublisher(RabbitMqOptions options, Action<string>? log = null)
    {
        // Retry with backoff: the service may start before RabbitMQ accepts connections.
        _connection = BrokerConnect.Open(options, "eshop-publisher", log ?? Console.WriteLine);
        _channel = _connection.CreateModel();
        Topology.Declare(_channel);
        _channel.ConfirmSelect();
    }

    public void Publish<T>(string routingKey, T message) where T : IEvent
    {
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.ContentType = "application/json";
        props.MessageId = message.EventId.ToString();
        props.Type = typeof(T).Name;
        props.CorrelationId = Correlation.Current ?? Correlation.NewId();   // follows the work across services
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        lock (_gate) // IModel is not thread-safe
        {
            _channel.BasicPublish(Exchanges.Events, routingKey, props, body);
            _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        }
    }

    public static T? Deserialize<T>(ReadOnlyMemory<byte> body) => JsonSerializer.Deserialize<T>(body.Span, Json);

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
