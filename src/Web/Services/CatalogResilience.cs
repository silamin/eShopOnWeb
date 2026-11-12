using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Microsoft.eShopWeb.Web.Services;

/// <summary>
/// Polly policies for the monolith's calls to the Catalog service (krav 7). One instance for the whole app
/// (singleton), so the circuit breaker's state is shared by every request.
/// Order, outermost first: retry → circuit breaker → timeout per attempt.
///  - Timeout per attempt: 1,5 s. A hanging Catalog counts as a failure instead of blocking the page.
///  - Retry: 2 retries after 200 ms and 400 ms (+ up to 100 ms jitter) on a transient failure: network
///    error, 5xx, 408 or a per-attempt timeout. A 4xx (e.g. 404) is not retried; it is an answer.
///  - Circuit breaker: after 5 handled failures in a row the circuit opens for 15 s. While it is open
///    no call is sent; BrokenCircuitException is thrown at once, which CatalogClient turns into
///    CatalogUnavailableException (the shop shows its "catalog unavailable" page in milliseconds instead
///    of after 3 × timeout). After 15 s one trial call is let through (half-open): success closes it.
/// </summary>
public sealed class CatalogResilience
{
    public const int RetryCount = 2;
    public const int FailuresBeforeBreaking = 5;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);

    public IAsyncPolicy<HttpResponseMessage> Retry { get; }
    public IAsyncPolicy<HttpResponseMessage> CircuitBreaker { get; }
    public IAsyncPolicy<HttpResponseMessage> Timeout { get; }

    public CatalogResilience(ILogger<CatalogResilience> log)
    {
        Retry = HttpPolicyExtensions
            .HandleTransientHttpError()                 // HttpRequestException, 5xx, 408
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(RetryCount,
                attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt) + Random.Shared.Next(0, 100)),
                (outcome, wait, attempt, _) => log.LogWarning(
                    "Catalog call failed ({Reason}); retry {Attempt}/{RetryCount} in {WaitMs} ms",
                    Reason(outcome), attempt, RetryCount, (int)wait.TotalMilliseconds));

        CircuitBreaker = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .CircuitBreakerAsync(FailuresBeforeBreaking, BreakDuration,
                onBreak: (outcome, wait) => log.LogError(
                    "Catalog circuit OPEN for {Seconds} s after {Failures} failures in a row (last: {Reason})",
                    wait.TotalSeconds, FailuresBeforeBreaking, Reason(outcome)),
                onReset: () => log.LogInformation("Catalog circuit CLOSED: a call succeeded"),
                onHalfOpen: () => log.LogInformation("Catalog circuit HALF-OPEN: next call is a trial"));

        Timeout = Policy.TimeoutAsync<HttpResponseMessage>(AttemptTimeout, TimeoutStrategy.Optimistic);
    }

    private static string Reason(DelegateResult<HttpResponseMessage> outcome) =>
        outcome.Exception?.GetType().Name ?? $"HTTP {(int)outcome.Result.StatusCode}";
}
