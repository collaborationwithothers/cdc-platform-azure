using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Lexfield.TaskApi.Changes;
using Lexfield.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lexfield.TaskApi.Tests.Changes;

[Collection(LexfieldContainers.Name)]
public sealed class ChangesFeedTests(SqlServerFixture sql)
{
    // V4 verifies that a change committed after a watermark was read must still be
    // returned by a query using that exact earlier watermark.
    [Fact]
    public async Task LateCommitIsReturnedByAnEarlierWatermark()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);

        await using var holder = new SqlConnection(context.ConnectionString);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.WorkflowTask
               SET State = 'Assigned', Version = Version + 1,
                   UpdatedAt = SYSUTCDATETIME(), UpdatedBy = 'holder'
             WHERE Id = @taskId;
            """,
            new { taskId }, holderTransaction));

        long watermark;
        await using (var reader = new SqlConnection(context.ConnectionString))
        {
            await reader.OpenAsync();
            watermark = await reader.ExecuteScalarAsync<long>(
                "SELECT CHANGE_TRACKING_CURRENT_VERSION();");
        }

        await holderTransaction.CommitAsync();

        using var client = CreateClient(context);
        var response = await client.GetAsync(
            $"/tenants/tenant-a/tasks/changes?since={watermark}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChangesResponse>();
        Assert.NotNull(body);
        var change = Assert.Single(body.Changes, entry => entry.TaskId == taskId);
        Assert.Equal(2, change.Version);
        Assert.True(body.NextSyncVersion > watermark);
    }

    // Retention cleanup and table re-enable can both invalidate a watermark, but
    // re-enable creates the stale state deterministically without waiting (V4).
    [Fact]
    public async Task StaleWatermarkReturns410Gone()
    {
        await using var context = await CreateContextAsync();
        await SeedTaskAsync(context.ConnectionString);

        await using (var admin = new SqlConnection(context.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                """
                ALTER TABLE dbo.WorkflowTask DISABLE CHANGE_TRACKING;
                ALTER TABLE dbo.WorkflowTask ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
                """);
        }

        using var client = CreateClient(context);
        var response = await client.GetAsync("/tenants/tenant-a/tasks/changes?since=0");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task ResponseShapeMatchesTheSharedContract()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString);
        using var client = CreateClient(context);

        var (response, output) = await GetWithLogsAsync(
            client, "/tenants/tenant-a/tasks/changes?since=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("changes", out var changes));
        Assert.True(json.TryGetProperty("nextSyncVersion", out var nextSyncVersion));
        Assert.Equal(System.Text.Json.JsonValueKind.Number, nextSyncVersion.ValueKind);
        var first = Assert.Single(changes.EnumerateArray());
        Assert.Equal(taskId, first.GetProperty("taskId").GetInt32());
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        var log = FindEvent(output, "TaskApi.ChangesFeedRead");
        Assert.NotNull(log);
        Assert.Equal(1, log.Value.GetProperty("changeCount").GetInt32());
    }

    [Fact]
    public async Task WithoutWatermarkReturnsEveryTaskInIdOrder()
    {
        await using var context = await CreateContextAsync();
        var firstTaskId = await SeedTaskAsync(context.ConnectionString);
        var secondTaskId = await SeedTaskAsync(context.ConnectionString);
        using var client = CreateClient(context);
        var response = await client.GetAsync("/tenants/tenant-a/tasks/changes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChangesResponse>();
        Assert.NotNull(body);
        Assert.Equal([(firstTaskId, 1), (secondTaskId, 1)],
            body.Changes.Select(change => (change.TaskId, change.Version)));
        Assert.True(body.NextSyncVersion > 0);
    }

    [Fact]
    public Task MissingTrackedTableReturns503AndLogsUnavailableEvent() =>
        AssertUnavailableAsync("DROP TABLE dbo.WorkflowTask;", "?since=0");

    [Fact]
    public Task BootstrapReturns503WhenDatabaseChangeTrackingIsDisabled() =>
        AssertUnavailableAsync(
            """
            ALTER TABLE dbo.WorkflowTask DISABLE CHANGE_TRACKING;
            ALTER DATABASE CURRENT SET CHANGE_TRACKING = OFF;
            """, "");

    [Fact]
    public async Task AnotherTenantTokenReturns403()
    {
        await using var context = await CreateContextAsync();
        using var client = CreateClient(context);

        var response = await client.GetAsync("/tenants/tenant-b/tasks/changes?since=0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTokenReturns401()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();

        var response = await client.GetAsync("/tenants/tenant-a/tasks/changes?since=0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<TestContext> CreateContextAsync()
    {
        var databaseName = $"TaskChanges{Guid.NewGuid():N}";
        var connection = await sql.CreateTenantDatabaseAsync(databaseName, "tenant-a");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest,
            $$"""[{"tenantId":"tenant-a","database":"{{databaseName}}","streamIsolated":false}]""");
        var factory = new ChangesApiFactory(connection, key, manifest, GetFreePort(), databaseName);
        return new TestContext(factory, connection, key, manifest);
    }

    private static async Task<int> SeedTaskAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT dbo.WorkflowTask (State, Version, CreatedAt, UpdatedAt, UpdatedBy)
            VALUES ('Created', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 'seed');
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """);
    }

    private async Task AssertUnavailableAsync(string setupSql, string query)
    {
        await using var context = await CreateContextAsync();
        await using var admin = new SqlConnection(context.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync(setupSql);
        using var client = CreateClient(context);
        var (response, output) = await GetWithLogsAsync(
            client, $"/tenants/tenant-a/tasks/changes{query}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var log = FindEvent(output, "TaskApi.ChangesFeedUnavailable");
        Assert.NotNull(log);
        Assert.Equal("Information", log.Value.GetProperty("level").GetString());
        Assert.Equal("Change Tracking is unavailable", log.Value.GetProperty("message").GetString());
    }

    private static HttpClient CreateClient(TestContext context)
    {
        var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", CreateToken("tenant-a", context.SigningKey));
        return client;
    }

    private static string CreateToken(string tenantId, string key)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://issuer.test", audience: "lexfield-task-api",
            claims: [new Claim("tenantId", tenantId), new Claim(JwtRegisteredClaimNames.Sub, "user:1")],
            notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
    }

    private static async Task<(HttpResponseMessage Response, string Output)> GetWithLogsAsync(
        HttpClient client, string path)
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var response = await client.GetAsync(path);
            return (response, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    private static JsonElement? FindEvent(string output, string eventName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("eventName", out var name)
                && name.GetString() == eventName) return document.RootElement.Clone();
        }
        return null;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TestContext(
        ChangesApiFactory Factory, string ConnectionString, string SigningKey, string ManifestPath)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            File.Delete(ManifestPath);
        }
    }

    private sealed class ChangesApiFactory(
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
}
