using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using System.Net;
using System.Text;
using System.Text.Json;
using CatalogService;
using Eshop.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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
    .Enrich.WithProperty("Service", "catalog")
    .Enrich.WithProperty("Host", Environment.MachineName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}@{Host} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .ReadFrom.Configuration(ctx.Configuration);
    // Seq (local log server, compose service "seq"; UI http://localhost:5341) when SEQ_URL is set.
    if (ctx.Configuration["SEQ_URL"] is { Length: > 0 } seqUrl) lc.WriteTo.Seq(seqUrl);
});

builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("CatalogConnection")));

// Defense in depth: the gateway checks the JWT on /api/*, and this service validates it again.
// Same signing key as the monolith's token issuer (Jwt:Key, set from the environment).
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");
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
builder.Services.AddAuthorization();

// Events out (krav 5): CatalogItemCreated, so Inventory creates a stock line. Broker settings from the environment.
builder.Services.AddSingleton(sp => new EventPublisher(RabbitMqOptions.FromEnvironment(),
    m => sp.GetRequiredService<ILoggerFactory>().CreateLogger("EventPublisher").LogWarning("{Retry}", m)));

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

// Two instances share catalog-db. Only the first one seeds (Catalog:Seed, default true; false on the second),
// so two seeders never race on the "ids 1..12" guard.
if (app.Configuration.GetValue("Catalog:Seed", true))
using (var scope = app.Services.CreateScope())
{
    var seeded = await Seed.RunAsync(scope.ServiceProvider.GetRequiredService<CatalogDbContext>(), app.Logger);
    // The 12 seeded items get stock lines the same way a new item does: through the event, never a shared table.
    var publisher = app.Services.GetRequiredService<EventPublisher>();
    foreach (var i in seeded) PublishCreated(publisher, i, app.Logger);
}

// Commit, then publish (with publisher confirms). If the publish fails, the item exists without a stock line:
// the known commit-then-publish gap (outbox not built), logged with the id so it can be re-sent.
static void PublishCreated(EventPublisher publisher, CatalogItem item, ILogger log)
{
    try
    {
        publisher.Publish(RoutingKeys.CatalogItemCreated,
            new CatalogItemCreated(Guid.NewGuid(), DateTimeOffset.UtcNow, item.Id, item.Name, item.Price));
    }
    catch (Exception ex)
    {
        log.LogError(ex, "CatalogItemCreated NOT published for item {Id}; Inventory has no stock line for it", item.Id);
    }
}

// Same error shape as the Public API's ExceptionMiddleware: 409 for duplicates, 500 otherwise.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = ex is DuplicateException ? (int)HttpStatusCode.Conflict : 500;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { statusCode = ctx.Response.StatusCode, message = ex.Message }));
    }
});

// Which instance answered: shows the gateway's load balancing (2 instances) in the response headers.
var instance = Environment.MachineName;
app.Use(async (ctx, next) => { ctx.Response.Headers["X-Catalog-Instance"] = instance; await next(); });

app.UseAuthentication();
app.UseAuthorization();

const string AdminRole = "Administrators";
const string PlaceholderHost = "http://catalogbaseurltobereplaced";
var catalogBaseUrl = app.Configuration["CatalogBaseUrl"] ?? "";

CatalogItemDto ToDto(CatalogItem i) => new()
{
    Id = i.Id,
    Name = i.Name,
    Description = i.Description,
    Price = i.Price,
    PictureUri = (i.PictureUri ?? "").Replace(PlaceholderHost, catalogBaseUrl),
    CatalogTypeId = i.CatalogTypeId,
    CatalogBrandId = i.CatalogBrandId,
};

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/catalog-items", async (int? pageSize, int? pageIndex, int? catalogBrandId, int? catalogTypeId, CatalogDbContext db) =>
{
    var size = pageSize ?? 0;
    var index = pageIndex ?? 0;
    var q = db.CatalogItems.AsNoTracking()
        .Where(i => (!catalogBrandId.HasValue || i.CatalogBrandId == catalogBrandId) &&
                    (!catalogTypeId.HasValue || i.CatalogTypeId == catalogTypeId));
    var total = await q.CountAsync();
    var page = q.OrderBy(i => i.Id);
    var items = size > 0
        ? await page.Skip(index * size).Take(size).ToListAsync()
        : await page.ToListAsync();   // pageSize 0 = all, as in the Public API
    var pageCount = size > 0 ? (int)Math.Ceiling((decimal)total / size) : (total > 0 ? 1 : 0);
    return Results.Ok(new { catalogItems = items.Select(ToDto), pageCount, totalItems = total });
});

// Batch lookup for the single read-only seam (ICatalogClient): one call for a whole basket/order.
app.MapGet("/api/catalog-items/by-ids", async ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "ids")] int[] ids, CatalogDbContext db) =>
{
    if (ids.Length == 0 || ids.Length > 200)
        return Results.BadRequest(new { statusCode = 400, message = "Provide between 1 and 200 ids." });
    var wanted = ids.Distinct().ToArray();
    var items = await db.CatalogItems.AsNoTracking().Where(i => wanted.Contains(i.Id)).OrderBy(i => i.Id).ToListAsync();
    return Results.Ok(new { catalogItems = items.Select(ToDto) });
});

app.MapGet("/api/catalog-items/{catalogItemId:int}", async (int catalogItemId, CatalogDbContext db) =>
{
    var item = await db.CatalogItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == catalogItemId);
    return item is null ? Results.NotFound() : Results.Ok(new { catalogItem = ToDto(item) });
});

app.MapPost("/api/catalog-items", [Authorize(Roles = AdminRole)] async (CreateCatalogItemRequest r, CatalogDbContext db, EventPublisher publisher) =>
{
    if (await db.CatalogItems.AnyAsync(i => i.Name == r.Name))
        throw new DuplicateException($"A catalogItem with name {r.Name} already exists");

    var item = new CatalogItem
    {
        CatalogTypeId = r.CatalogTypeId,
        CatalogBrandId = r.CatalogBrandId,
        Description = r.Description,
        Name = r.Name,
        Price = r.Price,
        // Uploads are disabled in eShopOnWeb (security note in the original); new items get the placeholder picture.
        PictureUri = "images\\products\\eCatalog-item-default.png?0",
    };
    db.CatalogItems.Add(item);
    await db.SaveChangesAsync();
    PublishCreated(publisher, item, app.Logger);
    var dto = ToDto(item);
    return Results.Created($"api/catalog-items/{dto.Id}", new { catalogItem = dto });
});

app.MapPut("/api/catalog-items", [Authorize(Roles = AdminRole)] async (UpdateCatalogItemRequest r, CatalogDbContext db) =>
{
    var errors = new Dictionary<string, string[]>();
    if (r.Id is < 1 or > 10000) errors["Id"] = new[] { "Id must be between 1 and 10000." };
    if (r.CatalogBrandId is < 1 or > 10000) errors["CatalogBrandId"] = new[] { "CatalogBrandId must be between 1 and 10000." };
    if (r.CatalogTypeId is < 1 or > 10000) errors["CatalogTypeId"] = new[] { "CatalogTypeId must be between 1 and 10000." };
    if (string.IsNullOrWhiteSpace(r.Name)) errors["Name"] = new[] { "Name is required." };
    if (string.IsNullOrWhiteSpace(r.Description)) errors["Description"] = new[] { "Description is required." };
    if (r.Price < 0.01M || r.Price > 10000M) errors["Price"] = new[] { "Price must be between 0.01 and 10000." };
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == r.Id);
    if (item is null) return Results.NotFound();
    item.Name = r.Name;
    item.Description = r.Description;
    item.Price = r.Price;
    item.CatalogBrandId = r.CatalogBrandId;
    item.CatalogTypeId = r.CatalogTypeId;
    await db.SaveChangesAsync();
    return Results.Ok(new { catalogItem = ToDto(item) });
});

app.MapDelete("/api/catalog-items/{catalogItemId:int}", [Authorize(Roles = AdminRole)] async (int catalogItemId, CatalogDbContext db) =>
{
    var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == catalogItemId);
    if (item is null) return Results.NotFound();
    db.CatalogItems.Remove(item);
    await db.SaveChangesAsync();
    return Results.Ok(new { status = "Deleted" });
});

app.MapGet("/api/catalog-brands", async (CatalogDbContext db) =>
    Results.Ok(new { catalogBrands = (await db.CatalogBrands.AsNoTracking().OrderBy(b => b.Id).ToListAsync()).Select(b => new CatalogBrandDto(b.Id, b.Brand)) }));

app.MapGet("/api/catalog-types", async (CatalogDbContext db) =>
    Results.Ok(new { catalogTypes = (await db.CatalogTypes.AsNoTracking().OrderBy(t => t.Id).ToListAsync()).Select(t => new CatalogTypeDto(t.Id, t.Type)) }));

app.Run();

public partial class Program { }
