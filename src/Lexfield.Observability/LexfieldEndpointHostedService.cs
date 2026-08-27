using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Lexfield.Observability;

internal sealed record LexfieldEndpointOptions(int Port, string ServiceName);

internal sealed class LexfieldEndpointHostedService : IHostedService, IDisposable
{
    private readonly LexfieldEndpointOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycleGate = new();
    private HttpListener? _listener;
    private Task? _serveTask;
    private bool _disposed;
    public LexfieldEndpointHostedService(LexfieldEndpointOptions options) => _options = options;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{_options.Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            listener.Close();
            throw new InvalidOperationException(
                $"{_options.ServiceName} observability health endpoint cannot start on TCP port {_options.Port}. " +
                "The process cannot answer its /healthz liveness or /readyz readiness probes; " +
                "both probes prove only that this process's HTTP listener answered, and /readyz " +
                "does not check downstream dependencies. " +
                $"Underlying listener error ({exception.ErrorCode}): {exception.Message}. " +
                "Check the configured address, process permission, and whether another process " +
                "already uses the port. If there is a port conflict, set " +
                "Lexfield:Observability:Port to an unused port and restart the service.",
                exception);
        }

        _listener = listener;
        _serveTask = ServeAsync(_stopping.Token);
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? serveTask;
        lock (_lifecycleGate)
        {
            if (!_disposed)
            {
                _stopping.Cancel();
                _listener?.Stop();
            }

            serveTask = _serveTask;
        }

        if (serveTask is not null)
        {
            await serveTask.WaitAsync(cancellationToken);
        }
    }
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _stopping.Cancel();
            _listener?.Close();
            _stopping.Dispose();
            _disposed = true;
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await WriteResponseAsync(context, cancellationToken);
        }
    }

    private async Task WriteResponseAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        // A health endpoint is a liveness probe, and a readiness endpoint is a
        // readiness probe. Both routes prove only that this process's HTTP
        // listener answered; /readyz does not check downstream dependencies.
        var path = context.Request.Url?.AbsolutePath;
        var (statusCode, contentType, body) = path switch
        {
            "/healthz" => (200, "text/plain; charset=utf-8", "ok\n"),
            "/readyz" => (200, "text/plain; charset=utf-8", "ready\n"),
            _ => (404, "text/plain; charset=utf-8", "not found\n")
        };

        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            context.Response.Close();
        }
    }
}
