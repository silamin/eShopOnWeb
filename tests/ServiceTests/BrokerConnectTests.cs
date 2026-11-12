using Eshop.Messaging;
using Xunit;

namespace ServiceTests;

public class BrokerConnectTests
{
    [Fact]
    public void Backoff_doubles_and_is_capped_at_10_seconds()
    {
        var waits = Enumerable.Range(1, 7).Select(a => BrokerConnect.Backoff(a).TotalSeconds).ToArray();

        Assert.Equal(new double[] { 1, 2, 4, 8, 10, 10, 10 }, waits);
    }

    [Fact]
    public async Task Unreachable_broker_is_retried_then_cancelled_cleanly()
    {
        var options = new RabbitMqOptions("127.0.0.1", 1, "u", "p", "/");   // port 1: nothing listens
        var retries = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrokerConnect.OpenAsync(options, "test", retries.Add, cts.Token));

        Assert.True(retries.Count >= 2, $"expected at least 2 logged retries, got {retries.Count}");
        Assert.All(retries, r => Assert.Contains("Broker not ready for 'test'", r));
    }
}
