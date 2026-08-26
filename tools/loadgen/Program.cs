using System.Globalization;
using System.Net.Http.Headers;
using Lexfield.LoadGen;

const string TokenVariable = "LEXFIELD_LOADGEN_TOKEN";

var settings = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["--base-address"] = "http://localhost:5000",
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

LoadOptions options;
Uri baseAddress;
try
{
    baseAddress = new Uri(settings["--base-address"], UriKind.Absolute);
    options = new LoadOptions
    {
        Distribution = TenantKeyDistribution.Parse(
            settings["--distribution"], int.Parse(settings["--tenants"], CultureInfo.InvariantCulture)),
        EventsPerSecond = double.Parse(settings["--rate"], CultureInfo.InvariantCulture),
        EventCount = int.Parse(settings["--events"], CultureInfo.InvariantCulture),
        Seed = int.Parse(settings["--seed"], CultureInfo.InvariantCulture),
    };
}
catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(Usage());
    return 2;
}

using var client = new HttpClient { BaseAddress = baseAddress };
var token = Environment.GetEnvironmentVariable(TokenVariable);
if (!string.IsNullOrWhiteSpace(token))
{
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
else
{
    Console.Error.WriteLine(
        $"No bearer token in {TokenVariable}; task-api will reject every request as unauthorised.");
}

// No Activity is started and no listener is attached, so task-api writes a null
// TraceParent and every run exercises the untraced write path.
var report = await new LoadRunner(client, options, TimeProvider.System, Console.Out)
    .RunAsync(CancellationToken.None);

Console.Error.WriteLine($"""
    Synthetic load run complete. Every task, tenant, and actor it wrote is synthetic.
      events issued:  {report.Issued}
      succeeded:      {report.Succeeded}
      failed:         {report.Failed}
      target rate:    {options.EventsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}/s
      observed rate:  {(report.Issued / report.Elapsed.TotalSeconds).ToString("0.##", CultureInfo.InvariantCulture)}/s
      tenants drawn:  {report.EventsPerTenant.Count} of {options.Distribution.Keys.Count}
    """);

return report.Failed == 0 ? 0 : 1;

static string Usage() => """
    Usage: loadgen [options]
      --base-address URL   task-api base address (default http://localhost:5000)
      --tenants N          synthetic tenant count (default 3)
      --distribution SPEC  uniform, or hot:COUNT:SHARE such as hot:8:0.8 (default uniform)
      --rate N             events per second (default 10)
      --events N           events to issue (default 100)
      --seed N             random seed, so a run is reproducible (default 1)
    The bearer token is read from the LEXFIELD_LOADGEN_TOKEN environment variable.
    """;
