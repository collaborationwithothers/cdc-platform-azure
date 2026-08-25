using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Dapper;
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

        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { actor = "user:1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTaskWithAnotherTenantTokenReturns403()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.PostAsJsonAsync("/tenants/tenant-b/tasks", new { actor = "user:1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateTaskUsesTheRouteTenantDatabaseAndWritesVersionOneTaskAndOutbox()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.PostAsJsonAsync(
            "/tenants/tenant-a/tasks", new { actor = "user:1", teamId = "team-a" });
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
        Assert.Equal("user:1", task.UpdatedBy);
        Assert.Equal("WorkflowTask", outbox.AggregateType);
        Assert.Equal($"tenant-a-{body.TaskId}", outbox.AggregateId);
        Assert.Equal("TaskTransitioned", outbox.EventType);
        Assert.Equal(1, outbox.Version);
        Assert.Contains("\"to\":\"Created\"", outbox.Payload);
        Assert.Contains("\"version\":1", outbox.Payload);
        Assert.Equal(0, await tenantB.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkflowTask"));
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
        var response = await client.PostAsJsonAsync("/tenants/tenant-a/tasks", new { actor = "user:1" });

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

        Assert.Equal("ok\n", await healthClient.GetStringAsync("http://localhost:8080/healthz"));
        Assert.Equal("ready\n", await healthClient.GetStringAsync("http://localhost:8080/readyz"));
    }

    private async Task<TestContext> CreateContextAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var databaseA = await sql.CreateTenantDatabaseAsync($"TaskApiA{suffix}", "tenant-a");
        var databaseB = await sql.CreateTenantDatabaseAsync($"TaskApiB{suffix}", "tenant-b");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var factory = new TaskApiFactory(databaseA, databaseB, key);
        return new TestContext(factory, databaseA, databaseB, key);
    }

    private static string CreateToken(string tenantId, string key)
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

    private sealed record TestContext(
        TaskApiFactory Factory,
        string TenantA,
        string TenantB,
        string SigningKey) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private sealed class TaskApiFactory(string tenantA, string tenantB, string signingKey)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Tenants:tenant-a"] = tenantA,
                    ["Tenants:tenant-b"] = tenantB,
                    ["Authentication:Authority"] = "https://issuer.test",
                    ["Authentication:Audience"] = "lexfield-task-api"
                }));
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority = null;
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
