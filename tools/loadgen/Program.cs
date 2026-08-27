using System.Globalization;
using System.Net.Http.Headers;

namespace Lexfield.LoadGen;

public sealed record LoadgenCliOptions(Uri BaseAddress, LoadOptions LoadOptions);

public static class LoadgenCli
{
    public const string TokenVariable = "LEXFIELD_LOADGEN_TOKEN";
    public const string OutputContext = "task-api is the HTTP service that owns task state. A workflow transition moves a task from one state to another. The change data capture (CDC) path reads committed database changes and delivers them as events. These measurements matter because stage-zero request times can later be compared with processing and delivery times.";

    private static readonly Dictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--base-address"] = "http://localhost:5000",
            ["--tenants"] = "3",
            ["--distribution"] = "uniform",
            ["--rate"] = "10",
            ["--events"] = "100",
            ["--seed"] = "1",
        };

    public static string Usage => """
        Synthetic load generator: sends synthetic workflow-task transitions to task-api,
        the HTTP service that owns task state and writes accepted changes.
        Usage: loadgen [options] (use --help to print this text)
          --base-address URL   absolute task-api URL (default http://localhost:5000)
          --tenants N          number of synthetic tenant keys (default 3)
          --distribution SPEC  tenant selection rule: uniform, or hot:COUNT:SHARE
                               such as hot:8:0.8 (default uniform)
          --rate EVENTS_PER_SECOND
                               target events per second (default 10)
          --events N           total synthetic events to issue (default 100)
          --seed N             random seed for the tenant selection sequence (default 1)
        A bearer token is a credential sent in the HTTP Authorization header.
        Read it from the LEXFIELD_LOADGEN_TOKEN environment variable.
        A tenant distribution is the rule that chooses the synthetic tenant for each event.
        A rate schedule makes event n due at start + n / EVENTS_PER_SECOND; it is not
        a fixed sleep after each response.
        Trace context is metadata that links a request to related logs and messages.
        This run sends no trace context, so each stage-zero record tests the untraced path.
        Stage zero is the client-side timestamp recorded when an HTTP request is issued.
        Stage-zero JSON records go to stdout; progress and the final report go to stderr.
        """;

    public static bool TryParse(IReadOnlyList<string> args, out LoadgenCliOptions options, out string error)
    {
        var settings = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            var option = args[i];
            if (!settings.ContainsKey(option))
            {
                options = default!;
                error = $"Unknown option '{option}'. Expected one of: {KnownOptionForms()}.";
                return false;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options = default!;
                error = $"Option '{option}' is missing its value. Expected form: {OptionForm(option)}.";
                return false;
            }

            settings[option] = args[i + 1];
        }

        if (!Uri.TryCreate(settings["--base-address"], UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            return Invalid("--base-address", settings["--base-address"],
                "--base-address URL, where URL is an absolute HTTP(S) address", out options, out error);
        }

        if (!int.TryParse(settings["--tenants"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var tenantCount) || tenantCount < 1)
        {
            return Invalid("--tenants", settings["--tenants"],
                "--tenants N, where N is a positive integer", out options, out error);
        }

        TenantKeyDistribution distribution;
        try
        {
            distribution = TenantKeyDistribution.Parse(settings["--distribution"], tenantCount);
        }
        catch (FormatException exception)
        {
            return Invalid("--distribution", settings["--distribution"],
                $"--distribution uniform or --distribution hot:COUNT:SHARE ({exception.Message.Split('\n')[0]})",
                out options, out error);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Invalid("--distribution", settings["--distribution"],
                $"--distribution uniform or --distribution hot:COUNT:SHARE ({exception.Message.Split('\n')[0]})",
                out options, out error);
        }

        if (!double.TryParse(settings["--rate"], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var eventsPerSecond)
            || !double.IsFinite(eventsPerSecond) || eventsPerSecond <= 0)
        {
            return Invalid("--rate", settings["--rate"],
                "--rate EVENTS_PER_SECOND, where EVENTS_PER_SECOND is a finite positive number",
                out options, out error);
        }

        if (!int.TryParse(settings["--events"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var eventCount) || eventCount < 0)
        {
            return Invalid("--events", settings["--events"],
                "--events N, where N is a non-negative integer", out options, out error);
        }

        if (!int.TryParse(settings["--seed"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var seed))
        {
            return Invalid("--seed", settings["--seed"],
                "--seed N, where N is an integer", out options, out error);
        }

        options = new LoadgenCliOptions(
            baseAddress,
            new LoadOptions
            {
                Distribution = distribution,
                EventsPerSecond = eventsPerSecond,
                EventCount = eventCount,
                Seed = seed,
            });
        error = string.Empty;
        return true;
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        HttpClient client,
        string? token,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (args is ["--help"] or ["-h"])
        {
            await stdout.WriteLineAsync(Usage);
            return 0;
        }

        if (!TryParse(args, out var parsed, out var error))
        {
            await stderr.WriteLineAsync(error);
            await stderr.WriteLineAsync(Usage);
            return 2;
        }

        client.BaseAddress = parsed.BaseAddress;
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            await stderr.WriteLineAsync(
                $"No bearer token found in {TokenVariable}. A bearer token is the credential " +
                "sent with an HTTP request so task-api can authenticate the caller. " +
                $"Set {TokenVariable} before running; task-api will reject requests without it.");
        }

        try
        {
            var report = await new LoadRunner(
                    client, parsed.LoadOptions, TimeProvider.System, stdout, stderr)
                .RunAsync(CancellationToken.None);
            await stderr.WriteLineAsync(FormatReport(report, parsed.LoadOptions));
            return report.Failed == 0 ? 0 : 1;
        }
        catch (LoadGenTransportException exception)
        {
            var endpoint = DisplayAddress(new Uri(client.BaseAddress!, exception.Endpoint).AbsoluteUri);
            await stderr.WriteLineAsync(OutputContext);
            await stderr.WriteLineAsync($"Transport failure during the {exception.Stage} stage.");
            await stderr.WriteLineAsync($"Request: POST {endpoint}");
            await stderr.WriteLineAsync(
                "Consequence: the run stopped before this event outcome was recorded locally.");
            await stderr.WriteLineAsync(
                "Safe correction: check that task-api is running at the base address and retry " +
                "only after checking task-api for a committed change.");
            return 1;
        }
        catch (LoadGenResponseException exception)
        {
            var endpoint = DisplayAddress(new Uri(client.BaseAddress!, exception.Endpoint).AbsoluteUri);
            await stderr.WriteLineAsync(OutputContext);
            await stderr.WriteLineAsync(
                $"Create stage: POST {endpoint} returned HTTP {exception.Status}, but its response was unusable.");
            await stderr.WriteLineAsync(
                "Consequence: the run stopped after task-api may have committed the create; " +
                "no duplicate create was sent for this tenant.");
            await stderr.WriteLineAsync($"Safe correction: {exception.Correction}");
            return 1;
        }
    }

    public static string FormatReport(LoadReport report, LoadOptions options) => $"""
        {OutputContext}
        Synthetic load run complete. The run sent synthetic workflow-task transitions to task-api.
        Configured inputs:
          events requested: {options.EventCount}
          target rate:      {options.EventsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}/s
          tenant keys:      {options.Distribution.Keys.Count}
        Observed measurements:
          events issued:    {report.Issued}
          succeeded:        {report.Succeeded}
          failed:           {report.Failed}
          tenants drawn:    {report.EventsPerTenant.Count} of {options.Distribution.Keys.Count}
        Derived values:
          observed rate:    {(report.Issued / report.Elapsed.TotalSeconds).ToString("0.##", CultureInfo.InvariantCulture)}/s
        Generated tenant keys, task payloads, and transition actor values are synthetic.
        Task IDs returned by task-api belong to this synthetic run. The task creation audit
        actor comes from the bearer token subject and may be a real test identity.
        """;

    private static bool Invalid(
        string option,
        string value,
        string expected,
        out LoadgenCliOptions options,
        out string error)
    {
        options = default!;
        error = $"Invalid value '{(option == "--base-address" ? DisplayAddress(value) : value)}' for option '{option}'. Expected form: {expected}.";
        return false;
    }

    private static string DisplayAddress(string value) =>
        Uri.TryCreate(value.Split(['?', '#'], 2)[0], UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path)
            : "<invalid URL>";

    private static string KnownOptionForms() => string.Join(", ",
        Defaults.Keys.Select(OptionForm));

    private static string OptionForm(string option) => option switch
    {
        "--base-address" => "--base-address URL",
        "--tenants" => "--tenants N",
        "--distribution" => "--distribution SPEC",
        "--rate" => "--rate EVENTS_PER_SECOND",
        "--events" => "--events N",
        "--seed" => "--seed N",
        _ => "--option value",
    };
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var httpClient = new HttpClient();
        return await LoadgenCli.RunAsync(
            args,
            httpClient,
            Environment.GetEnvironmentVariable(LoadgenCli.TokenVariable),
            Console.Out,
            Console.Error);
    }
}
