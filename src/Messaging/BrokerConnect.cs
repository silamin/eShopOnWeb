using RabbitMQ.Client;

namespace Eshop.Messaging;

/// <summary>
/// Opens the FIRST connection to the broker with retry and exponential backoff. Needed because a service
/// can start before RabbitMQ accepts AMQP connections (seen on Docker Desktop: SocketException 111,
/// "Connection refused", and the container exited). AutomaticRecoveryEnabled only re-opens a connection
/// that was once established; it does not cover the first one.
/// Backoff: 1, 2, 4, 8 s, then 10 s per attempt, until <see cref="MaxWait"/> has passed; then the last
/// error is thrown and the service stops (a broker that is still down after that is a real fault).
/// </summary>
public static class BrokerConnect
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);

    /// <summary>Delay before attempt n+1, after attempt n failed (n starts at 1).</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, Math.Min(attempt - 1, 4))));

    /// <summary>Blocking version, for constructors (EventPublisher is built at startup by DI).</summary>
    public static IConnection Open(RabbitMqOptions options, string clientName, Action<string>? log = null)
    {
        var factory = options.ToConnectionFactory();
        var started = DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try { return factory.CreateConnection(clientName); }
            catch (Exception ex) when (DateTime.UtcNow - started < MaxWait)
            {
                var wait = Backoff(attempt);
                log?.Invoke(Describe(clientName, attempt, wait, ex));
                Thread.Sleep(wait);
            }
        }
    }

    /// <summary>Non-blocking version, for BackgroundService.ExecuteAsync.</summary>
    public static async Task<IConnection> OpenAsync(RabbitMqOptions options, string clientName,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var factory = options.ToConnectionFactory();
        var started = DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try { return factory.CreateConnection(clientName); }
            catch (Exception ex) when (DateTime.UtcNow - started < MaxWait && !ct.IsCancellationRequested)
            {
                var wait = Backoff(attempt);
                log?.Invoke(Describe(clientName, attempt, wait, ex));
                await Task.Delay(wait, ct);
            }
        }
    }

    private static string Describe(string clientName, int attempt, TimeSpan wait, Exception ex) =>
        $"Broker not ready for '{clientName}' (attempt {attempt}, next in {wait.TotalSeconds:0} s): " +
        ex.GetBaseException().Message.Split('\n')[0];
}
