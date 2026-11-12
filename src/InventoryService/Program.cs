using Serilog;
using System.Text;
using InventoryService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Inventory Service (krav 3, lagerstyring), with its own database inventory-db (krav 6), fed by
// events (krav 5): CatalogItemCreated creates a stock line; OrderCreated reserves the order's stock
// (StockReserved / StockRejected back to Ordering); OrderCancelled releases it (the saga's compensation).

var builder = WebApplication.CreateBuilder(args);
// Serilog (krav 7): structured logs to the console, read with `docker compose logs`. Every event carries
// Service and Host (the container, so the two Catalog instances can be told apart). Levels can be
// changed without a rebuild, e.g. Serilog__MinimumLevel__Default=Debug in the environment.
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Yarp", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "inventory")
    .Enrich.WithProperty("Host", Environment.MachineName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}@{Host} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .ReadFrom.Configuration(ctx.Configuration));

builder.Services.AddDbContext<InventoryDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("InventoryConnection")));
builder.Services.AddScoped<ReservationHandler>();
builder.Services.AddSingleton(sp => new Eshop.Messaging.EventPublisher(Eshop.Messaging.RabbitMqOptions.FromEnvironment(),
    m => sp.GetRequiredService<ILoggerFactory>().CreateLogger("EventPublisher").LogWarning("{Retry}", m)));
builder.Services.AddHostedService<CatalogEventsConsumer>();
builder.Services.AddHostedService<OrderEventsConsumer>();

// Same JWT check as the Catalog service: the gateway checks the token, this service checks it again.
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
// One log event per HTTP request: method, path, status code, elapsed ms.
app.UseSerilogRequestLogging();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); break; }
        catch (Exception ex) when (attempt < 30)
        {
            app.Logger.LogWarning("inventory-db not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message.Split('\n')[0]);
            await Task.Delay(2000);
        }
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Stock is read by staff, not shown in the shop (decision "No stock on the catalog page"), so admin only.
app.MapGet("/api/inventory", async (InventoryDbContext db) =>
{
    var items = await db.StockItems.AsNoTracking().OrderBy(s => s.CatalogItemId).ToListAsync();
    return Results.Ok(new { stockItems = items.Select(ToDto), totalItems = items.Count });
}).RequireAuthorization("admin");

app.MapGet("/api/inventory/{catalogItemId:int}", async (int catalogItemId, InventoryDbContext db) =>
{
    var s = await db.StockItems.AsNoTracking().FirstOrDefaultAsync(x => x.CatalogItemId == catalogItemId);
    return s is null ? Results.NotFound() : Results.Ok(new { stockItem = ToDto(s) });
}).RequireAuthorization("admin");

app.MapGet("/api/inventory/reservations/{orderId:int}", async (int orderId, InventoryDbContext db) =>
{
    var r = await db.Reservations.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.OrderId == orderId);
    return r is null ? Results.NotFound() : Results.Ok(new
    {
        reservation = new { r.OrderId, Status = r.Status.ToString(), r.Reason, r.CreatedAt, r.ReleasedAt,
            Lines = r.Lines.Select(l => new { l.CatalogItemId, l.Quantity }) }
    });
}).RequireAuthorization("admin");

app.Run();

static object ToDto(StockItem s) => new { s.CatalogItemId, s.Name, s.QuantityInStore, s.IsAvailable, s.CreatedAt };

public partial class Program { }
