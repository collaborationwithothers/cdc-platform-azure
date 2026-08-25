using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lexfield.Observability;

internal sealed class LexfieldLogEnricherProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly string _serviceName;
    private readonly object _writeLock = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    public LexfieldLogEnricherProvider(string serviceName)
    {
        _serviceName = serviceName;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new LexfieldLogEnricherLogger(
            _serviceName,
            categoryName,
            _scopeProvider,
            _writeLock);
    }
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }
    public void Dispose() { }
}
internal sealed class LexfieldLogEnricherLogger : ILogger
{
    private readonly string _serviceName;
    private readonly string _categoryName;
    private readonly IExternalScopeProvider _scopeProvider;
    private readonly object _writeLock;
    public LexfieldLogEnricherLogger(
        string serviceName,
        string categoryName,
        IExternalScopeProvider scopeProvider,
        object writeLock)
    {
        _serviceName = serviceName;
        _categoryName = categoryName;
        _scopeProvider = scopeProvider;
        _writeLock = writeLock;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        return _scopeProvider.Push(state);
    }
    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddValues(values, state);
        _scopeProvider.ForEachScope(static (scope, target) => AddValues(target, scope), values);

        var line = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["service"] = _serviceName,
            ["level"] = logLevel.ToString(),
            ["eventName"] = values.GetValueOrDefault("eventName") ?? eventId.Name ?? _categoryName,
            ["traceparent"] = GetTraceParent(),
            ["tenantId"] = values.GetValueOrDefault("tenantId") ?? "unknown"
        };

        if (values.TryGetValue("taskId", out var taskId) && taskId is not null) line["taskId"] = taskId;
        if (values.TryGetValue("version", out var version) && version is not null) line["version"] = version;
        line["message"] = formatter(state, exception);

        if (exception is not null)
        {
            line["exception"] = exception.ToString();
        }
        var json = JsonSerializer.Serialize(line);
        lock (_writeLock)
        {
            Console.Out.WriteLine(json);
        }
    }
    private static void AddValues(Dictionary<string, object?> target, object? state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                target[pair.Key] = pair.Value;
            }
        }
    }
    private static string GetTraceParent()
    {
        var activity = Activity.Current;
        if (activity is null || activity.Id is null)
        {
            return "none";
        }

        // Activities without listeners can omit the W3C flags suffix. Restore it
        // here so every emitted line carries a valid traceparent value.
        return activity.IdFormat == ActivityIdFormat.W3C && activity.Id.Count('-') == 2
            ? $"{activity.Id}-{(activity.Recorded ? "01" : "00")}"
            : activity.Id;
    }
}
