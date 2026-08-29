using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Lexfield.Contracts;
using Lexfield.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lexfield.TaskApi.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class TaskApiTests(SqlServerFixture sql)
{
    [Fact]
    public async Task CreateTaskWithoutTokenReturns401()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTaskWithAnotherTenantTokenReturns403()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.PostAsJsonAsync("/tenants/tenant-b/tasks", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-x", context.SigningKey));
        response = await client.PostAsJsonAsync("/tenants/tenant-x/tasks", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTaskUsesTheRouteTenantDatabaseAndWritesVersionOneTaskAndOutbox()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var output = new StringWriter();
        var originalOutput = Console.Out;
        HttpResponseMessage response;
        try
        {
            Console.SetOut(output);
            response = await client.PostAsJsonAsync(
                "/tenants/tenant-a/tasks", new { teamId = "team-a" });
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        var body = await response.Content.ReadFromJsonAsync<CreateResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(1, body.Version);

        await using var tenantA = new SqlConnection(context.TenantA);
        await using var tenantB = new SqlConnection(context.TenantB);
        await tenantA.OpenAsync();
        await tenantB.OpenAsync();
        var task = await tenantA.QuerySingleAsync<TaskRow>(
            "SELECT State, Version, UpdatedBy FROM dbo.WorkflowTask WHERE Id = @Id", new { Id = body.TaskId });
        var outbox = await tenantA.QuerySingleAsync<OutboxRow>(
            "SELECT AggregateType, AggregateId, EventType, Version, Payload FROM dbo.Outbox WHERE AggregateId = @Id",
            new { Id = $"tenant-a-{body.TaskId}" });

        Assert.Equal("Created", task.State);
        Assert.Equal(1, task.Version);
        Assert.Equal("user:entra-tenant:user-object", task.UpdatedBy);
        Assert.Equal("WorkflowTask", outbox.AggregateType);
        Assert.Equal($"tenant-a-{body.TaskId}", outbox.AggregateId);
        Assert.Equal("TaskTransitioned", outbox.EventType);
        Assert.Equal(1, outbox.Version);
        Assert.Contains("\"to\":\"Created\"", outbox.Payload);
        Assert.Contains("\"version\":1", outbox.Payload);
        Assert.Equal(0, await tenantB.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkflowTask"));
        Assert.Contains("\"eventName\":\"TaskApi.TransitionCommitted\"", output.ToString());
        Assert.Contains("\"eventName\":\"TaskApi.OutboxWritten\"", output.ToString());
        Assert.Contains("\"tenantId\":\"tenant-a\"", output.ToString());
        Assert.Contains("\"taskId\":", output.ToString());
        Assert.Contains("\"version\":1", output.ToString());
    }

    [Fact]
    public async Task CreateTaskWritesTheSharedDelegatedActorContextToTheTaskAndEvent()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            "tenant-a", context.SigningKey,
            new Claim("tid", "entra-tenant"),
            new Claim("oid", "user-object"),
            new Claim("azp", "client-v2"),
            new Claim("appid", "client-v1")));

        var response = await client.PostAsJsonAsync(
            "/tenants/tenant-a/tasks", new { teamId = "team-a" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created);
        await using var connection = new SqlConnection(context.TenantA);
        await connection.OpenAsync();
        var updatedBy = await connection.QuerySingleAsync<string>(
            "SELECT UpdatedBy FROM dbo.WorkflowTask WHERE Id = @Id", new { Id = created.TaskId });
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT Payload FROM dbo.Outbox WHERE AggregateId = @Id", new { Id = $"tenant-a-{created.TaskId}" });
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(payload);

        Assert.Equal("user:entra-tenant:user-object", updatedBy);
        Assert.NotNull(taskEvent);
        Assert.Equal(updatedBy, taskEvent.Actor);
        Assert.Equal("client-v2", taskEvent.ClientApplicationId);
        Assert.Equal("delegated", taskEvent.PermissionMode);
    }

    [Theory]
    [InlineData("azp", "client-v2")]
    [InlineData("appid", "client-v1")]
    public async Task CreateTaskReadsEitherSupportedClientApplicationIdClaim(
        string claimType, string expectedClientApplicationId)
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            "tenant-a", context.SigningKey, new Claim(claimType, expectedClientApplicationId)));

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created);
        await using var connection = new SqlConnection(context.TenantA);
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT Payload FROM dbo.Outbox WHERE AggregateId = @Id", new { Id = $"tenant-a-{created.TaskId}" });
        Assert.Equal(expectedClientApplicationId,
            JsonSerializer.Deserialize<TransitionEvent>(payload)!.ClientApplicationId);
    }

    [Fact]
    public async Task CreateTaskRecordsAnAbsentClientApplicationIdAndClassifiesAnApplicationToken()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            "tenant-a", context.SigningKey, new Claim("idtyp", "app")));

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created);
        await using var connection = new SqlConnection(context.TenantA);
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT Payload FROM dbo.Outbox WHERE AggregateId = @Id", new { Id = $"tenant-a-{created.TaskId}" });
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(payload)!;
        Assert.Equal("workload:entra-tenant:user-object", taskEvent.Actor);
        Assert.Null(taskEvent.ClientApplicationId);
        Assert.Equal("application", taskEvent.PermissionMode);
    }

    [Fact]
    public async Task CreateTaskClassifiesARoleCarryingUserTokenWithoutScopeAsDelegated()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            "tenant-a", context.SigningKey,
            new Claim("roles", "Tasks.Write.All")));

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created);
        await using var connection = new SqlConnection(context.TenantA);
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT Payload FROM dbo.Outbox WHERE AggregateId = @Id", new { Id = $"tenant-a-{created.TaskId}" });
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(payload)!;
        Assert.Equal("user:entra-tenant:user-object", taskEvent.Actor);
        Assert.Equal("delegated", taskEvent.PermissionMode);
    }

    [Fact]
    public async Task CreateTaskClassifiesAUserIdentityTypeAsDelegated()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(
            "tenant-a", context.SigningKey, new Claim("idtyp", "user")));

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created);
        await using var connection = new SqlConnection(context.TenantA);
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT Payload FROM dbo.Outbox WHERE AggregateId = @Id", new { Id = $"tenant-a-{created.TaskId}" });
        Assert.Equal("delegated", JsonSerializer.Deserialize<TransitionEvent>(payload)!.PermissionMode);
    }

    [Fact]
    public async Task CreateTaskRejectsATokenWithoutIdentityType()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateTokenWithoutIdentityType(
            "tenant-a", context.SigningKey));

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTaskRejectsMissingRequiredActorClaims()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateTokenWithoutRequiredActorClaims(
            "tenant-a", context.SigningKey));

        var missingClaims = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, missingClaims.StatusCode);
    }

    [Fact]
    public async Task OutboxFailureRollsBackTheTask()
    {
        await using var context = await CreateContextAsync();
        await using (var connection = new SqlConnection(context.TenantA))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                CREATE TRIGGER dbo.FailTaskApiOutbox ON dbo.Outbox
                AFTER INSERT AS
                BEGIN
                    THROW 51000, 'forced outbox failure', 1;
                END
                """);
        }

        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));
        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = new SqlConnection(context.TenantA);
        await verify.OpenAsync();
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkflowTask"));
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Outbox"));
    }

    [Fact]
    public async Task InternalHealthEndpointsRespond()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        using var healthClient = new HttpClient();

        Assert.Equal("ok\n", await healthClient.GetStringAsync($"http://localhost:{context.HealthPort}/healthz"));
        Assert.Equal("ready\n", await healthClient.GetStringAsync($"http://localhost:{context.HealthPort}/readyz"));
    }

    private async Task<TestContext> CreateContextAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var databaseNameA = $"TaskApiA{suffix}";
        var databaseNameB = $"TaskApiB{suffix}";
        var databaseA = await sql.CreateTenantDatabaseAsync(databaseNameA, "tenant-a");
        var databaseB = await sql.CreateTenantDatabaseAsync(databaseNameB, "tenant-b");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest, $$"""
            [{"tenantId":"tenant-a","database":"{{databaseNameA}}","streamIsolated":false},{"tenantId":"tenant-b","database":"{{databaseNameB}}","streamIsolated":false}]
            """);
        var port = GetFreePort();
        var originalPort = Environment.GetEnvironmentVariable("Lexfield__Observability__Port");
        Environment.SetEnvironmentVariable("Lexfield__Observability__Port", port.ToString());
        var factory = new TaskApiFactory(databaseA, databaseB, key, manifest, port, databaseNameA, databaseNameB);
        return new TestContext(factory, databaseA, databaseB, key, manifest, port, originalPort);
    }

    private static string CreateToken(string tenantId, string key, params Claim[] additionalClaims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://issuer.test",
            audience: "lexfield-task-api",
            claims:
            [
                new Claim("tenantId", tenantId),
                new Claim(JwtRegisteredClaimNames.Sub, "user:1"),
                new Claim("tid", "entra-tenant"),
                new Claim("oid", "user-object"),
                .. WithDefaultIdentityType(additionalClaims)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateTokenWithoutRequiredActorClaims(string tenantId, string key)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://issuer.test",
            audience: "lexfield-task-api",
            claims: [new Claim("tenantId", tenantId), new Claim(JwtRegisteredClaimNames.Sub, "user:1")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateTokenWithoutIdentityType(string tenantId, string key)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://issuer.test",
            audience: "lexfield-task-api",
            claims:
            [
                new Claim("tenantId", tenantId),
                new Claim(JwtRegisteredClaimNames.Sub, "user:1"),
                new Claim("tid", "entra-tenant"),
                new Claim("oid", "user-object")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
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
        TaskApiFactory Factory,
        string TenantA,
        string TenantB,
        string SigningKey,
        string ManifestPath,
        int HealthPort,
        string? OriginalPort) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("Lexfield__Observability__Port", OriginalPort);
            File.Delete(ManifestPath);
        }
    }

    private sealed class TaskApiFactory(
        string tenantA, string tenantB, string signingKey, string manifest, int healthPort, string databaseNameA, string databaseNameB)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TenantManifest:Path"] = manifest,
                    [$"ConnectionStrings:{databaseNameA}"] = tenantA,
                    [$"ConnectionStrings:{databaseNameB}"] = tenantB,
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

    private sealed record CreateResponse(int TaskId, int Version);
    private sealed record TaskRow(string State, int Version, string UpdatedBy);
    private sealed record OutboxRow(string AggregateType, string AggregateId, string EventType, int Version, string Payload);
}

[CollectionDefinition(LexfieldContainers.Name)]
public sealed class TaskApiContainers : ICollectionFixture<SqlServerFixture>;
