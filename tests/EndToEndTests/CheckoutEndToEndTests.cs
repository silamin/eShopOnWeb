using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace EndToEndTests;

/// <summary>
/// End to end (one test, by design): a customer checks out in the shop and the order saga runs across
/// Web → RabbitMQ → Ordering → Inventory → Payment → Ordering, all through the gateway, until the order
/// is Paid. Runs against a stack that is already up:
///     docker compose up -d        then        dotnet test tests/EndToEndTests
/// Base URL: ESHOP_E2E_BASE_URL (default http://localhost:5100). Uses the seeded demo users.
/// </summary>
public class CheckoutEndToEndTests
{
    private static readonly Uri Base = new(Environment.GetEnvironmentVariable("ESHOP_E2E_BASE_URL") ?? "http://localhost:5100");
    private static readonly Regex Antiforgery = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static async Task<string> TokenFromAsync(HttpClient http, string path)
    {
        var html = await http.GetStringAsync(path);
        var m = Antiforgery.Match(html);
        Assert.True(m.Success, $"no antiforgery token on {path}");
        return m.Groups[1].Value;
    }

    private static async Task PostFormAsync(HttpClient http, string path, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = await TokenFromAsync(http, path == "/Basket" ? "/" : path);
        using var response = await http.PostAsync(path, new FormUrlEncodedContent(form));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"POST {path}: expected a redirect, got {(int)response.StatusCode}");
    }

    private sealed record Orders(List<OrderRow> orders);
    private sealed record OrderRow(int id, string buyerId, string status);
    private sealed record AuthResponse(string token);

    [Fact]
    public async Task Checkout_in_the_shop_ends_with_a_Paid_order()
    {
        using var shop = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(30) };
        using var api = new HttpClient { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(30) };

        // Admin token for the order API (read-only use), and the orders that exist before the checkout.
        var auth = await (await api.PostAsJsonAsync("/api/authenticate",
            new { username = "admin@microsoft.com", password = "Pass@word1" })).Content.ReadFromJsonAsync<AuthResponse>();
        api.DefaultRequestHeaders.Authorization = new("Bearer", auth!.token);
        var before = (await api.GetFromJsonAsync<Orders>("/api/orders"))!.orders.Select(o => o.id).ToHashSet();

        // The customer's path, as the browser does it: log in, add item 1, check out.
        await PostFormAsync(shop, "/Identity/Account/Login", new()
            { ["Input.Email"] = "demouser@microsoft.com", ["Input.Password"] = "Pass@word1" });
        await PostFormAsync(shop, "/Basket", new()
            { ["id"] = "1", ["name"] = ".NET Bot Black Sweatshirt", ["pictureUri"] = "/images/products/1.png", ["price"] = "19.50" });
        await PostFormAsync(shop, "/Basket/Checkout", new());

        // The saga is asynchronous: poll until the new order reaches a final state.
        OrderRow? order = null;
        for (var i = 0; i < 30; i++)
        {
            order = (await api.GetFromJsonAsync<Orders>("/api/orders"))!.orders
                .FirstOrDefault(o => !before.Contains(o.id) && o.buyerId == "demouser@microsoft.com");
            if (order?.status is "Paid" or "Cancelled") break;
            await Task.Delay(500);
        }

        Assert.NotNull(order);
        Assert.Equal("Paid", order!.status);
    }
}
