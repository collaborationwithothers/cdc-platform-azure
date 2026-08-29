using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Dapper;
using Lexfield.Contracts;
using Lexfield.TaskApi.Transitions;
using Lexfield.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
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
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            context.SigningKey, new Claim("idtyp", "app"), new Claim("azp", "transition-client")));
        var path = $"/tenants/tenant-a/tasks/{taskId}/transitions";
        var body = new { to = "Assigned", expectedVersion = 1, teamId = "team-a" };

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(path, body), client.PostAsJsonAsync(path, body));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        await using var connection = new SqlConnection(context.ConnectionString);
        var task = await connection.QuerySingleAsync<TaskRow>(
            "SELECT State, Version, UpdatedBy FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId });
        var outbox = await connection.QuerySingleAsync<OutboxRow>(
            "SELECT AggregateId, Version, Payload FROM dbo.Outbox");
        Assert.Equal(new TaskRow("Assigned", 2, "workload:entra-tenant:user-object"), task);
        Assert.Equal($"tenant-a-{taskId}", outbox.AggregateId);
        Assert.Equal(2, outbox.Version);
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(outbox.Payload);
        Assert.NotNull(taskEvent);
        Assert.Equal(task.UpdatedBy, taskEvent.Actor);
        Assert.Equal("transition-client", taskEvent.ClientApplicationId);
        Assert.Equal("application", taskEvent.PermissionMode);
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
        Assert.Equal(new TaskRow("Created", 1, "seed"), await verify.QuerySingleAsync<TaskRow>(
            "SELECT State, Version, UpdatedBy FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId }));
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
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(context.SigningKey));
        using var activity = new Activity("transition-test").Start();
        var traced = await client.PostAsJsonAsync(
            $"/tenants/tenant-a/tasks/{tracedTask}/transitions",
            new { to = "Assigned", expectedVersion = 1 });
        var traceParent = traced.Headers.GetValues("X-Test-Activity").Single();
        activity.Stop();
        using var untracedRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/tenants/tenant-a/tasks/{untracedTask}/transitions")
        {
            Content = JsonContent.Create(new { to = "Assigned", expectedVersion = 1 })
        };
        untracedRequest.Headers.Add("X-Test-No-Activity", "true");
        var untraced = await client.SendAsync(untracedRequest);
        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untraced.StatusCode);

        await using var connection = new SqlConnection(context.ConnectionString);
        var traces = (await connection.QueryAsync<string?>(
            "SELECT TraceParent FROM dbo.Outbox ORDER BY Id")).ToArray();
        Assert.Collection(traces,
            traced => Assert.Equal(traceParent, traced),
            untraced => Assert.Null(untraced));
    }

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

    private static string CreateToken(string key, params Claim[] additionalClaims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://issuer.test", audience: "lexfield-task-api",
            claims:
            [
                new Claim("tenantId", "tenant-a"),
                new Claim(JwtRegisteredClaimNames.Sub, "user:1"),
                new Claim("tid", "entra-tenant"),
                new Claim("oid", "user-object"),
                .. WithDefaultIdentityType(additionalClaims)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }

    private static IEnumerable<Claim> WithDefaultIdentityType(Claim[] additionalClaims)
    {
        if (!additionalClaims.Any(claim => claim.Type == "idtyp"))
            yield return new Claim("idtyp", "user");
        foreach (var claim in additionalClaims) yield return claim;
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
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, ActivityControlStartupFilter>());
        }
    }

    private sealed class ActivityControlStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, requestNext) =>
            {
                var activity = Activity.Current;
                if (context.Request.Headers.ContainsKey("X-Test-No-Activity")) Activity.Current = null;
                else if (activity?.Id is not null) context.Response.Headers["X-Test-Activity"] = activity.Id;
                try { await requestNext(); }
                finally { Activity.Current = activity; }
            });
            next(app);
        };
    }

    private sealed record TaskRow(string State, int Version, string UpdatedBy);
    private sealed record OutboxRow(string AggregateId, int Version, string Payload);
}
