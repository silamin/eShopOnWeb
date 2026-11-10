using System.Globalization;
using Eshop.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace PaymentService;

public enum PaymentStatus { Succeeded, Failed }

/// <summary>The stored decision for one order. OrderId is the key: a repeated PaymentRequested never pays twice.</summary>
public class Payment
{
    public int OrderId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var p = b.Entity<Payment>();
        p.ToTable("Payments");
        p.HasKey(x => x.OrderId);
        p.Property(x => x.OrderId).ValueGeneratedNever();
        p.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        p.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        p.Property(x => x.Reason).HasMaxLength(200);
    }
}

/// <summary>
/// Simulated payment, deterministic: an amount above PAYMENT_DECLINE_ABOVE is declined, anything else goes
/// through. Nothing is random, so the demo and the tests can trigger the compensation path on purpose.
/// </summary>
public sealed class PaymentDecider
{
    private readonly PaymentDbContext _db;
    private readonly decimal _declineAbove;

    public PaymentDecider(PaymentDbContext db, IConfiguration config)
    {
        _db = db;
        _declineAbove = decimal.TryParse(config["PAYMENT_DECLINE_ABOVE"], NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : 1000m;
    }

    public decimal DeclineAbove => _declineAbove;

    public async Task<(IEvent Outcome, string RoutingKey, bool Duplicate)> HandleAsync(PaymentRequested evt)
    {
        var stored = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == evt.OrderId);
        if (stored is not null) return (OutcomeOf(stored), KeyOf(stored), true);

        var payment = evt.Amount > _declineAbove
            ? new Payment { OrderId = evt.OrderId, Amount = evt.Amount, Status = PaymentStatus.Failed,
                Reason = $"Declined (simulated): amount {evt.Amount.ToString(CultureInfo.InvariantCulture)} is above {_declineAbove.ToString(CultureInfo.InvariantCulture)}" }
            : new Payment { OrderId = evt.OrderId, Amount = evt.Amount, Status = PaymentStatus.Succeeded };
        payment.DecidedAt = DateTimeOffset.UtcNow;
        _db.Payments.Add(payment);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.Payments.AsNoTracking().FirstAsync(p => p.OrderId == evt.OrderId);
            return (OutcomeOf(winner), KeyOf(winner), true);
        }
        return (OutcomeOf(payment), KeyOf(payment), false);
    }

    private static string KeyOf(Payment p) => p.Status == PaymentStatus.Succeeded ? RoutingKeys.PaymentSucceeded : RoutingKeys.PaymentFailed;

    private static IEvent OutcomeOf(Payment p) => p.Status == PaymentStatus.Succeeded
        ? new PaymentSucceeded(Guid.NewGuid(), DateTimeOffset.UtcNow, p.OrderId, p.Amount)
        : new PaymentFailed(Guid.NewGuid(), DateTimeOffset.UtcNow, p.OrderId, p.Amount, p.Reason ?? "");
}
