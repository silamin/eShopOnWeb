using Eshop.Messaging;
using Microsoft.Extensions.Configuration;
using PaymentService;
using Xunit;

namespace ServiceTests;

/// <summary>The simulated payment: deterministic decline above PAYMENT_DECLINE_ABOVE, idempotent per order.</summary>
public class PaymentDeciderTests
{
    private static PaymentDecider Decider(PaymentDbContext db, string? declineAbove = "100") =>
        new(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PAYMENT_DECLINE_ABOVE"] = declineAbove })
            .Build());

    private static PaymentRequested Request(int orderId, decimal amount) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, orderId, amount);

    [Theory]
    [InlineData(39.00, typeof(PaymentSucceeded), RoutingKeys.PaymentSucceeded)]
    [InlineData(100.00, typeof(PaymentSucceeded), RoutingKeys.PaymentSucceeded)]   // the limit itself is accepted
    [InlineData(117.00, typeof(PaymentFailed), RoutingKeys.PaymentFailed)]
    public async Task Amount_decides_the_outcome(decimal amount, Type expected, string expectedKey)
    {
        var (outcome, key, _) = await Decider(Db.Payment()).HandleAsync(Request(1, amount));

        Assert.IsType(expected, outcome);
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public async Task Same_order_twice_gives_the_stored_answer()
    {
        var db = Db.Payment();
        await Decider(db).HandleAsync(Request(5, 117m));

        var (outcome, _, duplicate) = await Decider(db).HandleAsync(Request(5, 117m));

        Assert.True(duplicate);
        Assert.IsType<PaymentFailed>(outcome);
        Assert.Single(db.Payments);
    }

    [Fact]
    public void Missing_setting_defaults_to_1000()
    {
        Assert.Equal(1000m, Decider(Db.Payment(), declineAbove: null).DeclineAbove);
    }
}
