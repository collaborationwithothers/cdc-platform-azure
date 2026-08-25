using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Diagnostics;
using Dapper;
using Lexfield.Contracts;
using Lexfield.TaskApi.Transitions;
using Lexfield.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lexfield.TaskApi.Tests.Transitions;

[Collection(LexfieldContainers.Name)]
public sealed class TransitionEndpointTests(SqlServerFixture sql)
{
    [Fact]
    public async Task ConcurrentTransitionsAdvanceExactlyOnce()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(context.SigningKey));
        var path = $"/tenants/tenant-a/tasks/{taskId}/transitions";
        var body = new { to = "Assigned", actor = "spoofed", expectedVersion = 1, teamId = "team-a" };

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(path, body), client.PostAsJsonAsync(path, body));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        await using var connection = new SqlConnection(context.ConnectionString);
        var task = await connection.QuerySingleAsync<TaskRow>(
            "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId });
        var outbox = await connection.QuerySingleAsync<OutboxRow>(
            "SELECT AggregateId, Version FROM dbo.Outbox");
        Assert.Equal(new TaskRow("Assigned", 2), task);
        Assert.Equal(new OutboxRow($"tenant-a-{taskId}", 2), outbox);
    }

    [Fact]
    public async Task OutboxFailureLeavesTaskUnchanged()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        await using (var setup = new SqlConnection(context.ConnectionString))
            await setup.ExecuteAsync("""
                CREATE TRIGGER dbo.FailTransitionOutbox ON dbo.Outbox AFTER INSERT AS
                BEGIN THROW 51000, 'forced outbox failure', 1; END
                """);
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(context.SigningKey));

        var response = await client.PostAsJsonAsync(
            $"/tenants/tenant-a/tasks/{taskId}/transitions",
            new { to = "Assigned", expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = new SqlConnection(context.ConnectionString);
        Assert.Equal(new TaskRow("Created", 1), await verify.QuerySingleAsync<TaskRow>(
            "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId }));
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Outbox"));
    }

    [Fact]
    public async Task IllegalTransitionReturns422()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(context.SigningKey));

        var response = await client.PostAsJsonAsync(
            $"/tenants/tenant-a/tasks/{taskId}/transitions",
            new { to = "Delivered", expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TransitionWritesCurrentActivityAndAllowsNoActivity()
    {
        await using var context = await CreateContextAsync();
        var tracedTask = await SeedTaskAsync(context.ConnectionString);
        var untracedTask = await SeedTaskAsync(context.ConnectionString);
        _ = context.Factory.CreateClient();
        var transition = new TaskTransition(
            context.Factory.Services.GetRequiredService<TenantCatalog>(),
            context.Factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TaskTransition>>());
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var activity = new Activity("transition-test").Start();

        string? traceParent;
        try
        {
            await transition.ExecuteAsync(Command(tracedTask), CancellationToken.None);
            traceParent = activity.Id;
            activity.Stop();
            Assert.Null(Activity.Current);
            await transition.ExecuteAsync(Command(untracedTask), CancellationToken.None);
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        await using var connection = new SqlConnection(context.ConnectionString);
        var traces = (await connection.QueryAsync<string?>(
            "SELECT TraceParent FROM dbo.Outbox ORDER BY Id")).ToArray();
        Assert.Collection(traces,
            traced => Assert.Equal(traceParent, traced),
            untraced => Assert.Null(untraced));
    }

    private static TransitionCommand Command(int taskId) => new(
        "tenant-a", taskId, TaskState.Assigned, "user:1", 1, null, null);

    private async Task<TestContext> CreateContextAsync()
    {
        var databaseName = $"TaskTransitions{Guid.NewGuid():N}";
        var connection = await sql.CreateTenantDatabaseAsync(databaseName, "tenant-a");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest,
            $$"""[{"tenantId":"tenant-a","database":"{{databaseName}}","streamIsolated":false}]""");
        var port = GetFreePort();
        var factory = new TransitionApiFactory(connection, key, manifest, port, databaseName);
        return new TestContext(factory, connection, key, manifest);
    }

    private static async Task<int> SeedTaskAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<int>("""
            INSERT dbo.WorkflowTask (State, Version, CreatedAt, UpdatedAt, UpdatedBy)
            VALUES ('Created', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 'seed');
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """);
    }

    private static string CreateToken(string key)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://issuer.test", audience: "lexfield-task-api",
            claims: [new Claim("tenantId", "tenant-a"), new Claim(JwtRegisteredClaimNames.Sub, "user:1")],
            notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TestContext(
        TransitionApiFactory Factory, string ConnectionString, string SigningKey, string ManifestPath)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            File.Delete(ManifestPath);
        }
    }

    private sealed class TransitionApiFactory(
        string connection, string signingKey, string manifest, int healthPort, string databaseName)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TenantManifest:Path"] = manifest,
                    [$"ConnectionStrings:{databaseName}"] = connection,
                    ["Authentication:Authority"] = "https://issuer.test",
                    ["Authentication:Audience"] = "lexfield-task-api",
                    ["Lexfield:Observability:Port"] = healthPort.ToString()
                }));
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority = "https://issuer.test";
                    options.ConfigurationManager = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = "https://issuer.test",
                        ValidateAudience = true,
                        ValidAudience = "lexfield-task-api",
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
                    };
                }));
        }
    }

    private sealed record TaskRow(string State, int Version);
    private sealed record OutboxRow(string AggregateId, int Version);
}
