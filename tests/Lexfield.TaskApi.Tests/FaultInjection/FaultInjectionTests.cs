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

namespace Lexfield.TaskApi.Tests.FaultInjection;

[Collection(LexfieldContainers.Name)]
public sealed class FaultInjectionTests(SqlServerFixture sql)
{
    [Fact]
    public async Task SuppressionIsRejectedByDefaultBeforeDatabaseRowsChange()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = AuthorisedClient(context);

        var response = await client.PostAsJsonAsync(
            $"/tenants/tenant-a/tasks/{taskId}/transitions?suppressOutbox=true",
            new { to = "Assigned", expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var connection = new SqlConnection(context.ConnectionString);
        Assert.Equal(new TaskRow("Created", 1), await connection.QuerySingleAsync<TaskRow>(
            "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Outbox"));
    }

    [Fact]
    public async Task ClosedGateStillWritesNormalTransitionAndOutbox()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = AuthorisedClient(context);

        var response = await client.PostAsJsonAsync(
            $"/tenants/tenant-a/tasks/{taskId}/transitions",
            new { to = "Assigned", expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = new SqlConnection(context.ConnectionString);
        Assert.Equal(new TaskRow("Assigned", 2), await connection.QuerySingleAsync<TaskRow>(
            "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId }));
        var outbox = await connection.QuerySingleAsync<OutboxRow>(
            "SELECT AggregateId, Version FROM dbo.Outbox");
        Assert.Equal($"tenant-a-{taskId}", outbox.AggregateId);
        Assert.Equal(2, outbox.Version);
    }

    [Fact]
    public async Task OpenGateCommitsTaskWithoutOutboxAndLogsFault()
    {
        await using var context = await CreateContextAsync(allowSuppression: true);
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = AuthorisedClient(context);
        var output = new StringWriter();
        var originalOutput = Console.Out;
        HttpResponseMessage response;
        try
        {
            Console.SetOut(output);
            response = await client.PostAsJsonAsync(
                $"/tenants/tenant-a/tasks/{taskId}/transitions?suppressOutbox=true",
                new { to = "Assigned", expectedVersion = 1 });
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = new SqlConnection(context.ConnectionString);
        Assert.Equal(new TaskRow("Assigned", 2), await connection.QuerySingleAsync<TaskRow>(
            "SELECT State, Version FROM dbo.WorkflowTask WHERE Id = @taskId", new { taskId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Outbox"));
        Assert.Contains("\"eventName\":\"TaskApi.FaultInjected\"", output.ToString());
    }

    private async Task<TestContext> CreateContextAsync(bool allowSuppression = false)
    {
        var databaseName = $"FaultInjection{Guid.NewGuid():N}";
        var connection = await sql.CreateTenantDatabaseAsync(databaseName, "tenant-a");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest,
            $$"""[{"tenantId":"tenant-a","database":"{{databaseName}}","streamIsolated":false}]""");
        var factory = new FaultInjectionApiFactory(connection, key, manifest, databaseName, allowSuppression);
        return new TestContext(factory, connection, key, manifest);
    }

    private static HttpClient AuthorisedClient(TestContext context)
    {
        var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken(context.SigningKey));
        return client;
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
            claims:
            [
                new Claim("tenantId", "tenant-a"),
                new Claim(JwtRegisteredClaimNames.Sub, "user:1"),
                new Claim("tid", "entra-tenant"),
                new Claim("oid", "user-object")
            ],
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
        FaultInjectionApiFactory Factory, string ConnectionString, string SigningKey, string ManifestPath)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            File.Delete(ManifestPath);
        }
    }

    private sealed class FaultInjectionApiFactory(
        string connection, string signingKey, string manifest, string databaseName,
        bool allowSuppression) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["TenantManifest:Path"] = manifest,
                    [$"ConnectionStrings:{databaseName}"] = connection,
                    ["Authentication:Authority"] = "https://issuer.test",
                    ["Authentication:Audience"] = "lexfield-task-api",
                    ["Lexfield:Observability:Port"] = GetFreePort().ToString()
                };
                if (allowSuppression) settings["Demo:AllowOutboxSuppression"] = "true";
                configuration.AddInMemoryCollection(settings);
            });
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
