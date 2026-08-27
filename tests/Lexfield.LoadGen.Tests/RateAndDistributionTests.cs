using Lexfield.LoadGen;
using Microsoft.Extensions.Time.Testing;

namespace Lexfield.LoadGen.Tests;

/// <summary>
/// The two things the ticket asks be provable without a network: that the rate
/// limiter holds the configured rate, and that the tenant key distribution is
/// configured rather than assumed.
/// </summary>
public class RateAndDistributionTests
{
    [Fact]
    public async Task The_run_issues_events_on_the_configured_rate_schedule()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var limiter = new RateLimiter(eventsPerSecond: 10, time);

        for (var issued = 0; issued < 10; issued++)
        {
            var delay = limiter.DelayBeforeNext();
            var waiting = limiter.WaitForNextAsync(CancellationToken.None);
            time.Advance(delay);
            await waiting;
        }

        // Ten events at 10/s: the first is due immediately and the tenth at 0.9 s.
        Assert.Equal(TimeSpan.FromSeconds(0.9), time.GetUtcNow() - start);
    }

    [Fact]
    public async Task The_rate_schedule_catches_up_after_the_run_falls_behind()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(eventsPerSecond: 10, time);
        time.Advance(TimeSpan.FromSeconds(1));

        // One second of schedule is already owed. Events 0 to 10 are due at 0 s
        // through 1.0 s, so eleven are issuable at once instead of each waiting
        // a further 100 ms.
        for (var issued = 0; issued < 11; issued++)
        {
            Assert.Equal(TimeSpan.Zero, limiter.DelayBeforeNext());
            await limiter.WaitForNextAsync(CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromSeconds(0.1), limiter.DelayBeforeNext());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_non_finite_or_non_positive_rate_is_rejected_before_a_run_starts(double rate)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RateLimiter(rate, TimeProvider.System));

        Assert.Contains("Events per second", error.Message, StringComparison.Ordinal);
        Assert.Contains("--rate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_uniform_distribution_spreads_events_across_every_synthetic_tenant()
    {
        var distribution = TenantKeyDistribution.Parse("uniform", tenantCount: 4);
        var counts = Draw(distribution, draws: 40_000);

        Assert.Equal(4, counts.Count);
        Assert.All(counts.Values, count => Assert.InRange(count, 9_000, 11_000));
    }

    [Fact]
    public void A_hot_distribution_sends_its_configured_share_to_the_hot_tenant_set()
    {
        var distribution = TenantKeyDistribution.Parse("hot:2:0.8", tenantCount: 10);
        var counts = Draw(distribution, draws: 40_000);

        var hot = distribution.Keys.Take(2).Sum(key => counts.GetValueOrDefault(key));
        Assert.InRange(hot / 40_000d, 0.78, 0.82);
    }

    [Fact]
    public void Every_tenant_key_is_labelled_synthetic()
        => Assert.All(
            TenantKeyDistribution.Parse("uniform", tenantCount: 3).Keys,
            key => Assert.StartsWith("synthetic-tenant-", key, StringComparison.Ordinal));

    [Theory]
    [InlineData("skewed")]
    [InlineData("hot:2")]
    [InlineData("hot:0:0.8")]
    [InlineData("hot:10:0.8")]
    [InlineData("hot:2:1.5")]
    [InlineData("hot:2:Infinity")]
    public void An_invalid_tenant_distribution_is_rejected_with_a_safe_error(string specification)
    {
        var error = Record.Exception(
            () => TenantKeyDistribution.Parse(specification, tenantCount: 10));

        Assert.True(error is (FormatException or ArgumentOutOfRangeException)
            && error.Message.Contains("tenant", StringComparison.OrdinalIgnoreCase),
            $"Distribution '{specification}' should be rejected with a tenant-specific safe error.");
    }

    private static Dictionary<string, int> Draw(TenantKeyDistribution distribution, int draws)
    {
        var random = new Random(Seed: 20260825);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var draw = 0; draw < draws; draw++)
        {
            var key = distribution.Next(random);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }
}
