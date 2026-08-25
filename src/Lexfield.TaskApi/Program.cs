using Lexfield.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddLexfieldObservability("TaskApi");
builder.Services.AddTaskApiAuthentication();
builder.Services.AddSingleton(provider => new TenantCatalog(
    provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<TaskCreation>();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapTaskEndpoints();
app.Run();

public partial class Program;
