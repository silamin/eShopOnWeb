// API gateway (strangler facade). Phase 0: everything is forwarded to the monolith.
// Routes and destinations live in appsettings.json (section "ReverseProxy") and can be overridden
// with environment variables, e.g. ReverseProxy__Clusters__web__Destinations__d1__Address.
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapGet("/gateway/health", () => Results.Ok(new { status = "ok" }));
app.MapReverseProxy();

app.Run();
