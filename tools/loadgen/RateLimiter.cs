namespace Lexfield.LoadGen;

/// <summary>
/// Paces the run so that event n is issued at start + n / eventsPerSecond.
/// </summary>
/// <remarks>
/// The schedule is absolute rather than a fixed sleep between events. A sleep
/// between events makes the offered rate depend on how long task-api took to
/// answer, so a slow server quietly turns a 50/s run into a 30/s run and every
/// figure measured from it describes a load nobody configured. With an absolute
/// schedule a caller that falls behind gets a zero delay until it has caught up,
/// so the run holds the configured rate across its whole length.
/// </remarks>
public sealed class RateLimiter
{
    private readonly TimeProvider time;
    private readonly double eventsPerSecond;
    private readonly long start;
    private long issued;

    public RateLimiter(double eventsPerSecond, TimeProvider time)
    {
        if (!double.IsFinite(eventsPerSecond) || eventsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventsPerSecond), eventsPerSecond,
                "Events per second must be greater than zero. Use a positive --rate value.");
        }

        this.eventsPerSecond = eventsPerSecond;
        this.time = time;
        start = time.GetTimestamp();
    }

    /// <summary>How long the next event must wait. Zero when it is already due.</summary>
    public TimeSpan DelayBeforeNext()
    {
        var due = TimeSpan.FromSeconds(issued / eventsPerSecond);
        var delay = due - time.GetElapsedTime(start);
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    public async Task WaitForNextAsync(CancellationToken cancellationToken)
    {
        var delay = DelayBeforeNext();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, time, cancellationToken);
        }

        issued++;
    }
}
