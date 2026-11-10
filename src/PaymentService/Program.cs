using Eshop.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService;

// Payment Service (krav 1: the third service, new), own database payment-db (krav 6), driven only by
// events (krav 5). Simulated and deterministic (PAYMENT_DECLINE_ABOVE). No HTTP API besides /health:
// nothing outside the saga talks to it, so it is not routed by the gateway.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<PaymentDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("PaymentConnection")));
builder.Services.AddScoped<PaymentDecider>();
builder.Services.AddSingleton(_ => new EventPublisher(RabbitMqOptions.FromEnvironment()));
builder.Services.AddHostedService<PaymentConsumer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); break; }
        catch (Exception ex) when (attempt < 30)
        {
            app.Logger.LogWarning("payment-db not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message.Split('\n')[0]);
            await Task.Delay(2000);
        }
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program { }
