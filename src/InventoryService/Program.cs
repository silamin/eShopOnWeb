using System.Text;
using InventoryService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Inventory Service (krav 3, lagerstyring), with its own database inventory-db (krav 6), fed by
// events (krav 5): CatalogItemCreated creates a stock line. Reservation at checkout (OrderCreated)
// and release on OrderCancelled are added with the Ordering service.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<InventoryDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("InventoryConnection")));
builder.Services.AddHostedService<CatalogEventsConsumer>();

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

app.Run();

static object ToDto(StockItem s) => new { s.CatalogItemId, s.Name, s.QuantityInStore, s.IsAvailable, s.CreatedAt };

public partial class Program { }
