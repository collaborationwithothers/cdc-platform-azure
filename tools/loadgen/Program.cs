using System.Globalization;
using System.Text.Json;
using Lexfield.LoadGen;

var settings = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["--tenants"] = "3",
    ["--distribution"] = "uniform",
    ["--rate"] = "10",
    ["--events"] = "100",
    ["--seed"] = "1",
};

for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !settings.ContainsKey(args[i]))
    {
        Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
        Console.Error.WriteLine(Usage());
        return 2;
    }

    settings[args[i]] = args[i + 1];
}

TenantKeyDistribution distribution;
double eventsPerSecond;
int eventCount, seed;
try
{
    distribution = TenantKeyDistribution.Parse(
        settings["--distribution"], int.Parse(settings["--tenants"], CultureInfo.InvariantCulture));
    eventsPerSecond = double.Parse(settings["--rate"], CultureInfo.InvariantCulture);
    eventCount = int.Parse(settings["--events"], CultureInfo.InvariantCulture);
    seed = int.Parse(settings["--seed"], CultureInfo.InvariantCulture);
}
catch (Exception exception) when (exception is FormatException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(Usage());
    return 2;
}

var time = TimeProvider.System;
var limiter = new RateLimiter(eventsPerSecond, time);
var random = new Random(seed);
var perTenant = new Dictionary<string, int>(StringComparer.Ordinal);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var started = time.GetTimestamp();

for (var issued = 0; issued < eventCount; issued++)
{
    await limiter.WaitForNextAsync(CancellationToken.None);
    var tenantId = distribution.Next(random);
    perTenant[tenantId] = perTenant.GetValueOrDefault(tenantId) + 1;
    Console.Out.WriteLine(JsonSerializer.Serialize(
        new { t0 = time.GetUtcNow(), tenantId, synthetic = true }, json));
}

var elapsed = time.GetElapsedTime(started);
Console.Error.WriteLine($"""
    Synthetic run planned. Every tenant it names is synthetic.
      events issued:  {eventCount}
      target rate:    {eventsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}/s
      observed rate:  {(eventCount / elapsed.TotalSeconds).ToString("0.##", CultureInfo.InvariantCulture)}/s
      tenants drawn:  {perTenant.Count} of {distribution.Keys.Count}
    """);

return 0;

static string Usage() => """
    Usage: loadgen [options]
      --tenants N          synthetic tenant count (default 3)
      --distribution SPEC  uniform, or hot:COUNT:SHARE such as hot:8:0.8 (default uniform)
      --rate N             events per second (default 10)
      --events N           events to issue, then stop (default 100)
      --seed N             random seed, so a run repeats exactly (default 1)
    """;
