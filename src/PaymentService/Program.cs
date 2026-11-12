using Serilog;
using Eshop.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService;

// Payment Service (krav 1: the third service, new), own database payment-db (krav 6), driven only by
// events (krav 5). Simulated and deterministic (PAYMENT_DECLINE_ABOVE). No HTTP API besides /health:
// nothing outside the saga talks to it, so it is not routed by the gateway.

var builder = WebApplication.CreateBuilder(args);
// Serilog (krav 7): structured logs to the console, read with `docker compose logs`. Every event carries
// Service and Host (the container, so the two Catalog instances can be told apart). Levels can be
// changed without a rebuild, e.g. Serilog__MinimumLevel__Default=Debug in the environment.
builder.Host.UseSerilog((ctx, lc) =>
{
    lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Yarp", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "payment")
    .Enrich.WithProperty("Host", Environment.MachineName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}@{Host} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .ReadFrom.Configuration(ctx.Configuration);
    // Seq (local log server, compose service "seq"; UI http://localhost:5341) when SEQ_URL is set.
    if (ctx.Configuration["SEQ_URL"] is { Length: > 0 } seqUrl) lc.WriteTo.Seq(seqUrl);
});
builder.Services.AddDbContext<PaymentDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("PaymentConnection")));
builder.Services.AddScoped<PaymentDecider>();
builder.Services.AddSingleton(sp => new EventPublisher(RabbitMqOptions.FromEnvironment(),
    m => sp.GetRequiredService<ILoggerFactory>().CreateLogger("EventPublisher").LogWarning("{Retry}", m)));
builder.Services.AddHostedService<PaymentConsumer>();

var app = builder.Build();
// Correlation id (krav 7): keep the caller's X-Correlation-Id or start one, log it on every line of this
// request, return it, and let EventPublisher put it on any message this request publishes.
app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers[Eshop.Messaging.Correlation.Header].FirstOrDefault() is { Length: > 0 } h ? h : Eshop.Messaging.Correlation.NewId();
    ctx.Request.Headers[Eshop.Messaging.Correlation.Header] = id;      // forwarded by the gateway (YARP copies request headers)
    ctx.Response.Headers[Eshop.Messaging.Correlation.Header] = id;
    Eshop.Messaging.Correlation.Current = id;
    using (Serilog.Context.LogContext.PushProperty(Eshop.Messaging.Correlation.LogProperty, id))
        await next(ctx);
});
// One log event per HTTP request: method, path, status code, elapsed ms.
app.UseSerilogRequestLogging();

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
