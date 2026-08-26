namespace Lexfield.LoadGen;

/// <summary>
/// Decides which synthetic tenant a generated event is attributed to.
/// </summary>
/// <remarks>
/// The shape is configured, never assumed. A uniform run and a run where a few
/// tenants take most of the traffic stress different things: uniform spreads
/// load across every connector, while a hot set concentrates it on one, which is
/// the shape the poison-event blast-radius measurement needs.
/// </remarks>
public sealed class TenantKeyDistribution
{
    private readonly string[] keys;
    private readonly int hotCount;
    private readonly double hotShare;

    private TenantKeyDistribution(string[] keys, int hotCount, double hotShare)
    {
        this.keys = keys;
        this.hotCount = hotCount;
        this.hotShare = hotShare;
    }

    /// <summary>Every tenant key this run can draw, in order. Index 0 onwards is the hot set.</summary>
    public IReadOnlyList<string> Keys => keys;

    /// <summary>
    /// Parses a distribution: <c>uniform</c>, or <c>hot:COUNT:SHARE</c> where
    /// COUNT tenants receive SHARE of the events, for example <c>hot:8:0.8</c>.
    /// </summary>
    public static TenantKeyDistribution Parse(string specification, int tenantCount)
    {
        if (tenantCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tenantCount), tenantCount, "A run needs at least one tenant.");
        }

        var keys = Enumerable.Range(1, tenantCount)
            .Select(n => $"synthetic-tenant-{n:D4}")
            .ToArray();

        if (specification.Equals("uniform", StringComparison.Ordinal))
        {
            return new TenantKeyDistribution(keys, hotCount: 0, hotShare: 0);
        }

        var parts = specification.Split(':');
        if (parts.Length != 3 || !parts[0].Equals("hot", StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var hot)
            || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var share))
        {
            throw new FormatException(
                $"Distribution '{specification}' is not 'uniform' or 'hot:COUNT:SHARE'.");
        }

        if (hot < 1 || hot >= tenantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(specification), hot,
                $"A hot set needs between 1 and {tenantCount - 1} tenants so a cold set remains.");
        }

        if (share is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(specification), share, "Hot share must be greater than 0 and at most 1.");
        }

        return new TenantKeyDistribution(keys, hot, share);
    }

    /// <summary>Draws the tenant key for the next event.</summary>
    public string Next(Random random)
    {
        if (hotCount == 0)
        {
            return keys[random.Next(keys.Length)];
        }

        // Cold draws exclude the hot set, so the hot set receives exactly the
        // configured share in expectation rather than the share plus its own
        // slice of a whole-population draw.
        return random.NextDouble() < hotShare
            ? keys[random.Next(hotCount)]
            : keys[hotCount + random.Next(keys.Length - hotCount)];
    }
}
