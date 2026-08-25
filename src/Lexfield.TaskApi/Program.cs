using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Lexfield.Contracts;
using Lexfield.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
var builder = WebApplication.CreateBuilder(args);
builder.AddLexfieldObservability("TaskApi");
builder.Services.AddSingleton(provider => new TenantCatalog(
    provider.GetRequiredService<IConfiguration>().GetSection("Tenants")));
builder.Services.AddSingleton<IAuthorizationHandler, TenantRouteHandler>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.Authority = configuration["Authentication:Authority"];
        options.Audience = configuration["Authentication:Audience"];
        options.RequireHttpsMetadata = true;
    });
builder.Services.AddAuthorization(options => options.AddPolicy(
    "TenantRoute",
    policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new TenantRouteRequirement());
    }));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapPost(
        "/tenants/{tenantId}/tasks",
        async (
            HttpContext http,
            string tenantId,
            CreateTaskRequest? request,
            TenantCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var connectionString = catalog.GetConnectionString(tenantId);
            if (connectionString is null)
            {
                return Results.NotFound();
            }
            var actor = request?.Actor
                ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? http.User.FindFirstValue("sub")
                ?? "unknown";
            var at = DateTimeOffset.UtcNow;
            var taskEvent = new TransitionEvent
            {
                TaskId = 0,
                From = null,
                To = TaskState.Created,
                Actor = actor,
                At = at,
                Version = 1,
                TeamId = request?.TeamId,
                AssigneeId = request?.AssigneeId
            };
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var taskId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT dbo.WorkflowTask (State, Version, TeamId, AssigneeId, CreatedAt, UpdatedAt, UpdatedBy)
                    VALUES (@State, @Version, @TeamId, @AssigneeId, @At, @At, @UpdatedBy);
                    SELECT CONVERT(int, SCOPE_IDENTITY());
                    """,
                    new
                    {
                        State = TaskState.Created.ToString(),
                        Version = 1,
                        request?.TeamId,
                        request?.AssigneeId,
                        At = at.UtcDateTime,
                        UpdatedBy = actor
                    },
                    transaction,
                    cancellationToken: cancellationToken));
                taskEvent = taskEvent with { TaskId = taskId };
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT dbo.Outbox
                        (AggregateType, AggregateId, EventType, Version, Payload, TraceParent)
                    VALUES
                        ('WorkflowTask', @AggregateId, 'TaskTransitioned', 1, @Payload, @TraceParent);
                    """,
                    new
                    {
                        AggregateId = $"{tenantId}-{taskId}",
                        Payload = JsonSerializer.Serialize(taskEvent),
                        TraceParent = System.Diagnostics.Activity.Current?.Id
                    },
                    transaction,
                    cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                return Results.Created(
                    $"/tenants/{tenantId}/tasks/{taskId}",
                    new CreateTaskResponse(taskId, 1));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
            }
        })
    .RequireAuthorization("TenantRoute");

app.Run();
public partial class Program;
public sealed record CreateTaskRequest(string? Actor, string? TeamId, string? AssigneeId);
public sealed record CreateTaskResponse(int TaskId, int Version);
public sealed class TenantCatalog
{
    private readonly IReadOnlyDictionary<string, string> _connections;
    public TenantCatalog(IConfigurationSection section)
    {
        _connections = section.GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!, StringComparer.Ordinal);
    }
    public string? GetConnectionString(string tenantId) =>
        _connections.GetValueOrDefault(tenantId);
}
public sealed class TenantRouteRequirement : IAuthorizationRequirement;
public sealed class TenantRouteHandler : AuthorizationHandler<TenantRouteRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantRouteRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && http.Request.RouteValues.TryGetValue("tenantId", out var routeTenant)
            && string.Equals(
                routeTenant?.ToString(),
                context.User.FindFirstValue("tenantId"),
                StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
