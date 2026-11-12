using Serilog;
using System.Text;
using Eshop.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderingService;

// Ordering Service (krav 1: carved out of the monolith), own database ordering-db (krav 6), and the
// orchestrator of the order saga (krav 5): see OrderSaga. Still to come: the buyer's order pages reading
// from here (the monolith still writes its own order too, as a Strangler transition step).

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
    .Enrich.WithProperty("Service", "ordering")
    .Enrich.WithProperty("Host", Environment.MachineName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}@{Host} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .ReadFrom.Configuration(ctx.Configuration);
    // Seq (local log server, compose service "seq"; UI http://localhost:5341) when SEQ_URL is set.
    if (ctx.Configuration["SEQ_URL"] is { Length: > 0 } seqUrl) lc.WriteTo.Seq(seqUrl);
});

builder.Services.AddDbContext<OrderingDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("OrderingConnection")));
builder.Services.AddScoped<OrderSaga>();
builder.Services.AddSingleton(sp => new EventPublisher(RabbitMqOptions.FromEnvironment(),
    m => sp.GetRequiredService<ILoggerFactory>().CreateLogger("EventPublisher").LogWarning("{Retry}", m)));
builder.Services.AddHostedService<SagaConsumer>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
        };
    });
builder.Services.AddAuthorization(o =>
    o.AddPolicy("admin", p => p.RequireAuthenticatedUser().RequireRole("Administrators")));

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
    var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); break; }
        catch (Exception ex) when (attempt < 30)
        {
            app.Logger.LogWarning("ordering-db not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message.Split('\n')[0]);
            await Task.Delay(2000);
        }
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/orders/{orderId:int}", async (int orderId, OrderingDbContext db) =>
{
    var o = await db.Orders.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == orderId);
    return o is null ? Results.NotFound() : Results.Ok(new
    {
        order = new
        {
            o.Id, o.BasketId, o.BuyerId, Status = o.Status.ToString(), o.CancelReason, o.OrderDate, o.Total,
            Lines = o.Lines.Select(l => new { l.CatalogItemId, l.ProductName, l.UnitPrice, l.Units }),
        }
    });
}).RequireAuthorization("admin");

app.MapGet("/api/orders", async (OrderingDbContext db) =>
{
    var orders = await db.Orders.AsNoTracking().OrderBy(o => o.Id)
        .Select(o => new { o.Id, o.BuyerId, Status = o.Status.ToString(), o.OrderDate }).ToListAsync();
    return Results.Ok(new { orders, totalItems = orders.Count });
}).RequireAuthorization("admin");

app.Run();

public partial class Program { }
