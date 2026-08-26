using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
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

namespace Lexfield.TaskApi.Tests.Repair;

[Collection(LexfieldContainers.Name)]
public sealed class RepairAndTenantInfoTests(SqlServerFixture sql)
{
    [Fact]
    public async Task RepairReadReturnsStateAndVersionMatchingTheRow()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(
            context.ConnectionString, "InProgress", 7, "team-conveyancing", "user:1234");
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.GetAsync($"/tenants/tenant-a/tasks/{taskId}");
        var body = await response.Content.ReadFromJsonAsync<RepairResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("InProgress", body.State);
        Assert.Equal(7, body.Version);
        Assert.Equal("team-conveyancing", body.TeamId);
        Assert.Equal("user:1234", body.AssigneeId);
    }

    [Fact]
    public async Task RepairReadForUnknownTaskReturns404()
    {
        await using var context = await CreateContextAsync();
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.GetAsync("/tenants/tenant-a/tasks/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TenantInfoReturnsTheClaimWrittenByOnboarding()
    {
        // The database's own claim is written by onboarding and is a different
        // fact from the route tenant id: the route maps to this database through
        // the catalog, while the claim is what onboarding stamped inside it. The
        // endpoint must return the database's claim, so it is provisioned here
        // with a value that is not the route tenant id to prove exactly that.
        await using var context = await CreateContextAsync(claim: "lexfield-claimed-007");
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("tenant-a", context.SigningKey));

        var response = await client.GetAsync("/tenants/tenant-a/info");
        var body = await response.Content.ReadFromJsonAsync<TenantInfoResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("lexfield-claimed-007", body.TenantId);
    }

    [Fact]
    public async Task BothRoutesRejectAMismatchedTokenAndAMissingToken()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString, "Created", 1, null, null);
        using var client = context.Factory.CreateClient();
        var repairPath = $"/tenants/tenant-a/tasks/{taskId}";
        const string infoPath = "/tenants/tenant-a/info";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(repairPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(infoPath)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("tenant-b", context.SigningKey));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(repairPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(infoPath)).StatusCode);
    }

    [Fact]
    public async Task RepairReadCarriesTheCallersTraceOnwardAndRunsWithoutOne()
    {
        await using var context = await CreateContextAsync();
        var taskId = await SeedTaskAsync(context.ConnectionString, "Created", 1, null, null);
        using var client = context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken("tenant-a", context.SigningKey));

        // Traced: a production caller sends its trace as a W3C traceparent header
        // and ASP.NET continues it, so the read runs inside the caller's trace and
        // the enricher stamps the caller's trace id on the log line. The read
        // starts no activity of its own, so it never forks a fresh one.
        // WebApplicationFactory's in-memory client does not inject the header, so
        // the test sets it the way HttpClient would in production.
        var output = new StringWriter();
        var originalOutput = Console.Out;
        using var caller = new Activity("repair-caller");
        caller.SetIdFormat(ActivityIdFormat.W3C);
        caller.Start();
        using var tracedRequest = new HttpRequestMessage(HttpMethod.Get, $"/tenants/tenant-a/tasks/{taskId}");
        tracedRequest.Headers.Add("traceparent", caller.Id);
        HttpResponseMessage traced;
        try
        {
            Console.SetOut(output);
            traced = await client.SendAsync(tracedRequest);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        caller.Stop();

        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
        var repairLine = FindEvent(output.ToString(), "TaskApi.RepairRead");
        Assert.NotNull(repairLine);
        var traceParent = repairLine.Value.GetProperty("traceparent").GetString();
        Assert.NotNull(traceParent);
        Assert.NotEqual("none", traceParent);
        Assert.Contains(caller.TraceId.ToString(), traceParent);

        // Untraced: the load generator drives this path with no ambient activity,
        // so the read must still answer and simply stamp no trace.
        using var untracedRequest = new HttpRequestMessage(HttpMethod.Get, $"/tenants/tenant-a/tasks/{taskId}");
        untracedRequest.Headers.Add("X-Test-No-Activity", "true");
        var untraced = await client.SendAsync(untracedRequest);

        Assert.Equal(HttpStatusCode.OK, untraced.StatusCode);
    }

    private async Task<TestContext> CreateContextAsync(string claim = "tenant-a")
    {
        var databaseName = $"RepairInfo{Guid.NewGuid():N}";
        var connection = await sql.CreateTenantDatabaseAsync(databaseName, claim);
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest,
            $$"""[{"tenantId":"tenant-a","database":"{{databaseName}}","streamIsolated":false}]""");
        var port = GetFreePort();
        var factory = new RepairApiFactory(connection, key, manifest, port, databaseName);
        return new TestContext(factory, connection, key, manifest);
    }

    private static async Task<int> SeedTaskAsync(
        string connectionString, string state, int version, string? teamId, string? assigneeId)
    {
        await using var connection = new SqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT dbo.WorkflowTask (State, Version, TeamId, AssigneeId, CreatedAt, UpdatedAt, UpdatedBy)
            VALUES (@state, @version, @teamId, @assigneeId, SYSUTCDATETIME(), SYSUTCDATETIME(), 'seed');
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """, new { state, version, teamId, assigneeId }));
    }

    private static JsonElement? FindEvent(string output, string eventName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("eventName", out var name)
                && name.GetString() == eventName)
            {
                return document.RootElement.Clone();
            }
        }
        return null;
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

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TestContext(
        RepairApiFactory Factory, string ConnectionString, string SigningKey, string ManifestPath)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            File.Delete(ManifestPath);
        }
    }

    private sealed record RepairResponse(string State, int Version, string? TeamId, string? AssigneeId);
    private sealed record TenantInfoResponse(string TenantId, DateTime ClaimedAt);

    private sealed class RepairApiFactory(
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
                try { await requestNext(); }
                finally { Activity.Current = activity; }
            });
            next(app);
        };
    }
}
