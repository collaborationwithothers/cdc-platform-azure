using Lexfield.QueueBuilder;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddQueueBuilder();
await builder.Build().RunAsync();
