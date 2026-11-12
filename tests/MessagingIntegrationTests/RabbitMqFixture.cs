using Eshop.Messaging;
using Testcontainers.RabbitMq;
using Xunit;

namespace MessagingIntegrationTests;

/// <summary>
/// A real RabbitMQ (the same image as docker-compose.yml) in a throwaway container, one per test class.
/// Needs a running Docker (Docker Desktop locally, the runner's Docker in GitHub Actions).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management")
        .WithUsername("eshop")
        .WithPassword("test-password")
        .Build();

    public RabbitMqOptions Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Options = new RabbitMqOptions(_container.Hostname, _container.GetMappedPublicPort(5672), "eshop", "test-password", "/");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
