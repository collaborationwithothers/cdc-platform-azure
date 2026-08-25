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
    provider.GetRequiredService<IConfiguration>()));
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
    })
    .Validate(options => !string.IsNullOrWhiteSpace(options.Authority)
        && !string.IsNullOrWhiteSpace(options.Audience),
        "Authentication:Authority and Authentication:Audience are required.");
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
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var connectionString = catalog.GetConnectionString(tenantId);
            if (connectionString is null)
            {
                return Results.NotFound();
            }
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
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
            var commitAttempted = false;
            async Task RollbackSafelyAsync()
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception rollbackFailure)
                {
                    logger.LogError(rollbackFailure, "Task rollback failed for tenant {tenantId}", tenantId);
                }
            }
            void LogEvent(string eventName, int taskId)
            {
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["eventName"] = eventName, ["tenantId"] = tenantId, ["taskId"] = taskId, ["version"] = 1
                })) logger.LogInformation("Task API event");
            }
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
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
                LogEvent("TaskApi.TransitionCommitted", taskId);
                LogEvent("TaskApi.OutboxWritten", taskId);
                return Results.Created(
                    $"/tenants/{tenantId}/tasks/{taskId}",
                    new CreateTaskResponse(taskId, 1));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!commitAttempted) await RollbackSafelyAsync();
                throw;
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "Task creation failed for tenant {tenantId}", tenantId);
                if (!commitAttempted) await RollbackSafelyAsync();
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
            }
        })
    .RequireAuthorization("TenantRoute");

app.Run();
public partial class Program;
public sealed record CreateTaskRequest(string? TeamId, string? AssigneeId);
public sealed record CreateTaskResponse(int TaskId, int Version);
public sealed class TenantCatalog
{
    private readonly IReadOnlyDictionary<string, string> _connections;
    public TenantCatalog(IConfiguration configuration)
    {
        var path = configuration["TenantManifest:Path"]
            ?? throw new InvalidOperationException("TenantManifest:Path is required.");
        var entries = JsonSerializer.Deserialize<List<TenantManifestEntry>>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Tenant manifest must contain an array.");
        _connections = entries.ToDictionary(
            entry => entry.TenantId,
            entry => configuration.GetConnectionString(entry.Database)
                ?? throw new InvalidOperationException($"ConnectionStrings:{entry.Database} is required."),
            StringComparer.Ordinal);
    }
    public string? GetConnectionString(string tenantId) =>
        _connections.GetValueOrDefault(tenantId);
}
public sealed record TenantManifestEntry(string TenantId, string Database, bool StreamIsolated);
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
