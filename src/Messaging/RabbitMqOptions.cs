using RabbitMQ.Client;

namespace Eshop.Messaging;

/// <summary>
/// Broker connection settings. Read from the environment (the same RABBITMQ_* names as .env), so no
/// user, password or vhost is written in code or in a settings file. There is no default user: a
/// missing value is an error, not "guest".
/// </summary>
public sealed record RabbitMqOptions(string Host, int Port, string User, string Password, string VHost)
{
    public static RabbitMqOptions FromEnvironment()
    {
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
                ? v
                : throw new InvalidOperationException($"{name} is not set (copy .env.example to .env).");

        return new RabbitMqOptions(
            Host: Environment.GetEnvironmentVariable("RABBITMQ_HOST") is { Length: > 0 } h ? h : "rabbitmq",
            Port: int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var p) ? p : 5672,
            User: Required("RABBITMQ_USER"),
            Password: Required("RABBITMQ_PASSWORD"),
            VHost: Required("RABBITMQ_VHOST"));
    }

    public ConnectionFactory ToConnectionFactory() => new()
    {
        HostName = Host,
        Port = Port,
        UserName = User,
        Password = Password,
        VirtualHost = VHost,
        DispatchConsumersAsync = false,
        AutomaticRecoveryEnabled = true,
    };
}
