// API gateway (strangler facade). Everything is forwarded to the monolith unless a route says otherwise.
// Routes and destinations live in appsettings.json (section "ReverseProxy") and can be overridden
// with environment variables, e.g. ReverseProxy__Clusters__web__Destinations__d1__Address.
//
// JWT: the gateway validates the bearer token with the same signing key as the issuer (monolith and Public API)
// and the Catalog service (Jwt:Key, from JWT_KEY in .env). It enforces it on routes that name the "admin" policy
// (catalog writes). Every other route stays anonymous at the gateway; the services still check their own endpoints.
//
// Rate limiting: every /api route carries the "api" policy (RateLimiterPolicy in appsettings.json): a fixed
// window per client IP, RateLimiting:PermitLimit requests per RateLimiting:WindowSeconds (default 100 per 10 s).
// Over the limit the gateway answers 429 itself; the request never reaches a service.
using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured (JWT_KEY in .env).");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

var permitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimit", 100);
var window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:WindowSeconds", 10));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = ((int)window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
});

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/gateway/health", () => Results.Ok(new { status = "ok" }));
app.MapReverseProxy();

app.Run();
