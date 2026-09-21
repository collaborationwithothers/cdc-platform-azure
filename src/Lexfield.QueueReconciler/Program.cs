using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueReconciler;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddQueueReconciler();
        await builder.Build().RunAsync();
    }
}
