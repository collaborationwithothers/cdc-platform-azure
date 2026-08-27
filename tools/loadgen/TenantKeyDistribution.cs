using System.Globalization;

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
    /// Tenant keys always use the fixed <c>synthetic-tenant-</c> prefix so the
    /// generator cannot silently target a different tenant identity.
    /// </summary>
    public static TenantKeyDistribution Parse(string specification, int tenantCount)
    {
        if (tenantCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tenantCount), tenantCount,
                "A run needs at least one synthetic tenant. Use --tenants N where N is at least 1.");
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
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hot)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
        {
            throw new FormatException(
                $"Tenant distribution '{specification}' is invalid. Use 'uniform' or " +
                "'hot:COUNT:SHARE', for example 'hot:8:0.8'.");
        }

        if (hot < 1 || hot >= tenantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(specification), hot,
                $"A hot tenant set needs between 1 and {tenantCount - 1} tenants so a " +
                "cold tenant set remains for the other events. Use hot:COUNT:SHARE.");
        }

        if (!double.IsFinite(share) || share <= 0 || share > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(specification), share,
                "Hot tenant share must be greater than 0 and at most 1. Use a decimal " +
                "fraction such as 0.8 in hot:COUNT:SHARE.");
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
