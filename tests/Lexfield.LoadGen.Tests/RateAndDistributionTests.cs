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
    public async Task Rate_limiter_holds_the_configured_rate()
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
    public async Task Rate_limiter_catches_up_rather_than_stretching_the_run()
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

    [Fact]
    public void Rate_limiter_rejects_a_rate_that_is_not_positive()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new RateLimiter(eventsPerSecond: 0, TimeProvider.System));

    [Fact]
    public void Uniform_distribution_spreads_events_across_every_tenant()
    {
        var distribution = TenantKeyDistribution.Parse("uniform", tenantCount: 4);
        var counts = Draw(distribution, draws: 40_000);

        Assert.Equal(4, counts.Count);
        Assert.All(counts.Values, count => Assert.InRange(count, 9_000, 11_000));
    }

    [Fact]
    public void Hot_distribution_sends_the_configured_share_to_the_hot_tenants()
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
    public void A_distribution_that_cannot_be_honoured_is_rejected(string specification)
        => Assert.ThrowsAny<Exception>(
            () => TenantKeyDistribution.Parse(specification, tenantCount: 10));

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
