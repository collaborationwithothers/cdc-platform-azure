using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Lexfield.Observability;

internal sealed record LexfieldEndpointOptions(string ServiceName, int Port);

internal sealed class LexfieldEndpointHostedService : IHostedService, IDisposable
{
    private readonly LexfieldEndpointOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private Task? _serveTask;

    public LexfieldEndpointHostedService(LexfieldEndpointOptions options)
    {
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{_options.Port}/");
        _listener.Start();
        _serveTask = ServeAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _listener?.Stop();

        if (_serveTask is not null)
        {
            await _serveTask.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Close();
        _stopping.Dispose();
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
        var path = context.Request.Url?.AbsolutePath;
        var (statusCode, contentType, body) = path switch
        {
            "/healthz" => (200, "text/plain; charset=utf-8", "ok\n"),
            "/readyz" => (200, "text/plain; charset=utf-8", "ready\n"),
            "/metrics" => (200, "text/plain; version=0.0.4; charset=utf-8", MetricsBody()),
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

    private string MetricsBody()
    {
        var service = _options.ServiceName.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"# HELP lexfield_observability_up The observability endpoint is accepting requests.\n" +
            $"# TYPE lexfield_observability_up gauge\n" +
            $"lexfield_observability_up{{service=\"{service}\"}} 1\n";
    }
}
