using Lexfield.Notifier;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddNotifier();
await builder.Build().RunAsync();
