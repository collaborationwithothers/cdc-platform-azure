using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Lexfield.QueueReconciler;
using Lexfield.QueueStore;
using Lexfield.TaskApi;
using Lexfield.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Lexfield.QueueReconciler.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class PassOneTests(SqlServerFixture sql)
{
    [Fact]
    public async Task SweepHost_two_hosts_renew_one_lease_and_report_one_empty_sweep()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync();
        await context.TransitionTaskAsync();
        await context.SeedQueueStateAsync(1);
        var events = new ConcurrentQueue<SweepEvent>();
        using var firstDelay = new DelayHandler(TimeSpan.FromMilliseconds(250));
        using var secondDelay = new DelayHandler(TimeSpan.FromMilliseconds(250));
        using var first = context.BuildHost(events, firstDelay);
        using var second = context.BuildHost(events, secondDelay);
        await first.StartAsync();
        await second.StartAsync();

        var outcomes = await Task.WhenAll(
            first.Services.GetRequiredService<SweepHost>().TickAsync(),
            second.Services.GetRequiredService<SweepHost>().TickAsync());

        Assert.Contains(SweepOutcome.Completed, outcomes);
        Assert.Contains(SweepOutcome.NotLeaseHolder, outcomes);
        Assert.Single(events, item => item.Name == "Reconciler.SweepStarted");
        var completed = Assert.Single(events,
            item => item.Name == "Reconciler.SweepCompleted");
        Assert.Equal(1, completed.ChangeCount);
        Assert.True(await context.WatermarkAsync() > context.InitialSourceVersion);
    }

    [Fact]
    public async Task SweepHost_lease_loss_fences_the_next_tenant_watermark()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync(includeSecondTenant: true);
        await context.TransitionTaskAsync();
        await context.SeedQueueStateAsync(1);
        var events = new ConcurrentQueue<SweepEvent>();
        using var gate = new RequestGate(blockRequest: 2);
        using var host = context.BuildHost(events, gate, ["tenant-a", "tenant-b"]);
        await host.StartAsync();

        var sweep = host.Services.GetRequiredService<SweepHost>().TickAsync();
        await gate.Blocked;
        var firstWatermark = await context.WatermarkAsync("tenant-a");
        await context.StealLeaseAsync();
        gate.Release();

        Assert.Equal(SweepOutcome.LeaseLost, await sweep);
        Assert.True(firstWatermark > context.InitialSourceVersion);
        Assert.Equal(context.SecondInitialSourceVersion, await context.WatermarkAsync("tenant-b"));
        Assert.DoesNotContain(events, item => item.Name == "Reconciler.SweepCompleted");
    }

    [Fact]
    public async Task SweepHost_reentrant_tick_is_skipped_and_counted()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync();
        await context.SeedQueueStateAsync(1);
        var measurements = new ConcurrentQueue<long>();
        var events = new ConcurrentQueue<SweepEvent>();
        using var listener = MetricListener(measurements);
        using var gate = new RequestGate(blockRequest: 1);
        using var host = context.BuildHost(events, gate, interval: TimeSpan.FromMilliseconds(30));
        await host.StartAsync();

        await gate.Blocked;
        Assert.True(SpinWait.SpinUntil(
            () => measurements.Sum() > 0, TimeSpan.FromSeconds(2)));
        gate.Release();

        Assert.True(SpinWait.SpinUntil(
            () => events.Any(item => item.Name == "Reconciler.SweepCompleted"),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Assert.Single(events,
            item => item.Name == "Reconciler.SweepCompleted").ChangeCount);
    }

    [Fact]
    public async Task Mismatch_is_recorded_with_the_real_task_api_and_queue_store()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync();
        await context.TransitionTaskAsync();
        await context.SeedQueueStateAsync(1);

        var result = await context.RunAsync();

        Assert.Equal(PassOneStatus.Completed, result.Status);
        Assert.Equal(1, result.ChangeCount);
        var observation = await context.QueryAsync<Observation>(
            "SELECT TaskId, SourceVersion, QueueVersion FROM dbo.DriftObservation;");
        Assert.Equal(new(1, 2, 1), observation);
        Assert.True(await context.WatermarkAsync() > context.InitialSourceVersion);
    }

    [Fact]
    public async Task Matching_task_removes_a_prior_observation()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync();
        await context.TransitionTaskAsync();
        await context.SeedQueueStateAsync(2);
        await context.SeedObservationAsync(1, 99, 1);

        var result = await context.RunAsync();

        Assert.Equal(PassOneStatus.Completed, result.Status);
        Assert.Equal(0, await context.CountAsync("DriftObservation"));
    }

    [Fact]
    public async Task Missing_queue_task_is_recorded_with_a_null_queue_version()
    {
        await using var context = await CreateContextAsync();
        await context.SetWatermarkAsync();
        await context.CreateTaskAsync();

        var result = await context.RunAsync();

        Assert.Equal(PassOneStatus.Completed, result.Status);
        var observation = await context.QueryAsync<Observation>(
            "SELECT TaskId, SourceVersion, QueueVersion FROM dbo.DriftObservation;");
        Assert.Equal(new(1, 1, null), observation);
    }

    [Fact]
    public async Task Empty_feed_advances_the_watermark_with_zero_changes()
    {
        await using var context = await CreateContextAsync();
        await context.CreateTaskAsync();
        await context.SetWatermarkAsync();
        await context.SeedQueueStateAsync(1);

        var result = await context.RunAsync();

        Assert.Equal(PassOneStatus.Completed, result.Status);
        Assert.Equal(0, result.ChangeCount);
        Assert.True(await context.WatermarkAsync() >= context.InitialSourceVersion);
        Assert.Equal(0, await context.CountAsync("DriftObservation"));
    }

    [Fact]
    public async Task Missing_or_aged_out_watermark_leaves_state_unchanged()
    {
        {
            await using var missing = await CreateContextAsync();
            await missing.CreateTaskAsync();
            var missingResult = await missing.RunAsync();
            Assert.Equal(PassOneStatus.WatermarkMissing, missingResult.Status);
            Assert.Equal(0, await missing.CountAsync("ReconcilerWatermark"));
        }

        await using var aged = await CreateContextAsync();
        await aged.CreateTaskAsync();
        await aged.SetWatermarkAsync(0);
        await aged.SeedObservationAsync(1, 7, null);
        await aged.AgeOutChangeTrackingAsync();
        var agedResult = await aged.RunAsync();
        Assert.Equal(PassOneStatus.FeedUnavailable, agedResult.Status);
        Assert.Equal(TaskApiChangesStatus.WatermarkAgedOut, agedResult.FeedStatus);
        Assert.Equal(0, await aged.WatermarkAsync());
        Assert.Equal(1, await aged.CountAsync("DriftObservation"));
    }

    private async Task<TestContext> CreateContextAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantName = $"ReconcilerTenantA{suffix}";
        var secondTenantName = $"ReconcilerTenantB{suffix}";
        var queueName = $"ReconcilerQueue{suffix}";
        var tenant = await sql.CreateTenantDatabaseAsync(tenantName, "tenant-a");
        var secondTenant = await sql.CreateTenantDatabaseAsync(secondTenantName, "tenant-b");
        var queue = await sql.CreateQueueStoreDatabaseAsync(queueName);
        var key = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest,
            $$"""[{"tenantId":"tenant-a","database":"{{tenantName}}","streamIsolated":false},{"tenantId":"tenant-b","database":"{{secondTenantName}}","streamIsolated":false}]""");
        return new TestContext(tenant, secondTenant, queue, key, manifest,
            new TaskApiFactory(tenant, secondTenant, tenantName, secondTenantName,
                key, manifest, ReservePort()));
    }

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record Observation(int TaskId, int SourceVersion, int? QueueVersion);

    private sealed class TestContext(
        string tenantConnectionString, string secondTenantConnectionString,
        string queueConnectionString, string signingKey,
        string manifestPath, TaskApiFactory taskApi) : IAsyncDisposable
    {
        public long InitialSourceVersion { get; private set; }
        public long SecondInitialSourceVersion { get; private set; }

        public async Task CreateTaskAsync()
        {
            using var client = AuthorizedClient();
            var response = await client.PostAsJsonAsync(
                "/tenants/tenant-a/tasks", new { teamId = "team-a" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public async Task TransitionTaskAsync()
        {
            using var client = AuthorizedClient();
            var response = await client.PostAsJsonAsync(
                "/tenants/tenant-a/tasks/1/transitions",
                new { to = "Assigned", expectedVersion = 1, teamId = "team-a" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task SetWatermarkAsync(long? version = null, bool includeSecondTenant = false)
        {
            await using var tenant = new SqlConnection(tenantConnectionString);
            await tenant.OpenAsync();
            InitialSourceVersion = version ?? await tenant.ExecuteScalarAsync<long>(
                "SELECT CHANGE_TRACKING_CURRENT_VERSION();");
            await using var queue = new SqlConnection(queueConnectionString);
            await queue.OpenAsync();
            await queue.ExecuteAsync(
                "INSERT dbo.ReconcilerWatermark (TenantId, SyncVersion, UpdatedAt) VALUES ('tenant-a', @version, SYSUTCDATETIME());",
                new { version = InitialSourceVersion });
            if (includeSecondTenant)
            {
                await using var secondTenant = new SqlConnection(secondTenantConnectionString);
                await secondTenant.OpenAsync();
                SecondInitialSourceVersion = await secondTenant.ExecuteScalarAsync<long>(
                    "SELECT CHANGE_TRACKING_CURRENT_VERSION();");
                await queue.ExecuteAsync(
                    "INSERT dbo.ReconcilerWatermark (TenantId, SyncVersion, UpdatedAt) VALUES ('tenant-b', @version, SYSUTCDATETIME());",
                    new { version = SecondInitialSourceVersion });
            }
        }

        public async Task SeedQueueStateAsync(int version) =>
            await new QueueStateStore(queueConnectionString).ApplyAsync(
                new QueueStateUpdate("tenant-a", 1, Lexfield.Contracts.TaskState.Assigned,
                    version, "team-a", null));

        public async Task SeedObservationAsync(int taskId, int source, int? queue)
        {
            await using var connection = new SqlConnection(queueConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "INSERT dbo.DriftObservation (TenantId, TaskId, SourceVersion, QueueVersion, FirstSeenAt) VALUES ('tenant-a', @taskId, @source, @queue, SYSUTCDATETIME());",
                new { taskId, source, queue });
        }

        public async Task AgeOutChangeTrackingAsync()
        {
            await using var connection = new SqlConnection(tenantConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "ALTER TABLE dbo.WorkflowTask DISABLE CHANGE_TRACKING; ALTER TABLE dbo.WorkflowTask ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);");
        }

        public async Task<PassOneResult> RunAsync()
        {
            var lease = await new ReconcilerStateStore(queueConnectionString)
                .TryAcquireLeaseAsync(TimeSpan.FromMinutes(1));
            Assert.NotNull(lease);
            using var handler = taskApi.Server.CreateHandler();
            using var client = new HttpClient(handler) { BaseAddress = new("http://task-api.test/") };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Token());
            return await new PassOne(
                new ReconcilerStateStore(queueConnectionString),
                new QueueStateStore(queueConnectionString),
                new TaskApiChangesClient(client, new TaskApiTokenProvider(
                    new Dictionary<string, string> { ["tenant-a"] = Token() })))
                .RunAsync(lease, "tenant-a");
        }

        public async Task<T> QueryAsync<T>(string sql)
        {
            await using var connection = new SqlConnection(queueConnectionString);
            await connection.OpenAsync();
            return await connection.QuerySingleAsync<T>(sql);
        }

        public Task<long> WatermarkAsync() => WatermarkAsync("tenant-a");

        public async Task<long> WatermarkAsync(string tenantId)
        {
            await using var connection = new SqlConnection(queueConnectionString);
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<long>(
                "SELECT SyncVersion FROM dbo.ReconcilerWatermark WHERE TenantId = @tenantId;",
                new { tenantId });
        }

        public async Task StealLeaseAsync()
        {
            await using var connection = new SqlConnection(queueConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "UPDATE dbo.SweepLease SET Owner = 'stolen', ExpiresAt = DATEADD(minute, 1, SYSUTCDATETIME()) WHERE Id = 1;");
        }

        public IHost BuildHost(
            ConcurrentQueue<SweepEvent> events,
            DelegatingHandler handler,
            string[]? tenantIds = null,
            TimeSpan? interval = null)
        {
            handler.InnerHandler = taskApi.Server.CreateHandler();
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QueueStore"] = queueConnectionString,
                ["TenantManifest:Path"] = manifestPath,
                ["QueueReconciler:TaskApiBaseAddress"] = "http://task-api.test/",
                ["QueueReconciler:TaskApiBearerTokens:tenant-a"] = CreateToken(signingKey),
                ["QueueReconciler:TaskApiBearerTokens:tenant-b"] = CreateToken(signingKey, "tenant-b"),
                ["QueueReconciler:Interval"] = (interval ?? TimeSpan.FromHours(1)).ToString(),
                ["QueueReconciler:LeaseDuration"] = TimeSpan.FromMilliseconds(120).ToString(),
                ["QueueReconciler:RenewalPeriod"] = TimeSpan.FromMilliseconds(30).ToString(),
                ["Lexfield:Observability:Port"] = ReservePort().ToString()
            });
            builder.AddQueueReconciler();
            var settings = SweepSettings.From(builder.Configuration);
            builder.Services.AddSingleton(settings with { TenantIds = tenantIds ?? ["tenant-a"] });
            builder.Services.AddHttpClient<TaskApiChangesClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            builder.Logging.AddProvider(new SweepLoggerProvider(events));
            return builder.Build();
        }

        public async Task<int> CountAsync(string table)
        {
            await using var connection = new SqlConnection(queueConnectionString);
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.[{table}];");
        }

        private HttpClient AuthorizedClient()
        {
            var client = taskApi.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Token());
            return client;
        }

        private string Token() => CreateToken(signingKey);

        public async ValueTask DisposeAsync()
        {
            await taskApi.DisposeAsync();
            File.Delete(manifestPath);
        }
    }

    private sealed class TaskApiFactory(
        string tenantConnectionString, string secondTenantConnectionString,
        string tenantDatabaseName, string secondTenantDatabaseName, string signingKey,
        string manifest, int port) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TenantManifest:Path"] = manifest,
                    [$"ConnectionStrings:{tenantDatabaseName}"] = tenantConnectionString,
                    [$"ConnectionStrings:{secondTenantDatabaseName}"] = secondTenantConnectionString,
                    ["Authentication:Authority"] = "https://issuer.test",
                    ["Authentication:Audience"] = "lexfield-task-api",
                    ["Lexfield:Observability:Port"] = port.ToString()
                }));
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme, options =>
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
                            IssuerSigningKey = new SymmetricSecurityKey(
                                Encoding.UTF8.GetBytes(signingKey))
                        };
                    }));
        }
    }

    private static string CreateToken(string key, string tenantId = "tenant-a")
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://issuer.test", audience: "lexfield-task-api",
            claims:
            [
                new("tenantId", tenantId), new(JwtRegisteredClaimNames.Sub, "user:1"),
                new("tid", "entra-tenant"), new("oid", "user-object"),
                new("scp", "Tasks.Write"), new("idtyp", "user")
            ], notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: credentials));
    }

    private static MeterListener MetricListener(ConcurrentQueue<long> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (instrument.Meter.Name == "Lexfield.QueueReconciler" &&
                instrument.Name == "reconciler.sweep.skipped") candidate.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measurements.Enqueue(value));
        listener.Start();
        return listener;
    }

    private sealed class DelayHandler(TimeSpan delay) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RequestGate(int blockRequest) : DelegatingHandler
    {
        private readonly TaskCompletionSource blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;
        public Task Blocked => blocked.Task;
        public void Release() => released.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requests) == blockRequest)
            {
                blocked.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record SweepEvent(string Name, int? ChangeCount);

    private sealed class SweepLoggerProvider(ConcurrentQueue<SweepEvent> events)
        : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public ILogger CreateLogger(string categoryName) => new SweepLogger(events, () => scopes);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public void Dispose() { }

        private sealed class SweepLogger(
            ConcurrentQueue<SweepEvent> events, Func<IExternalScopeProvider> scopes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                scopes().Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string? name = null;
                int? count = null;
                scopes().ForEachScope((scope, _) =>
                {
                    if (scope is not IEnumerable<KeyValuePair<string, object?>> values) return;
                    foreach (var pair in values)
                    {
                        if (pair.Key == "eventName") name = pair.Value as string;
                        if (pair.Key == "changeCount") count = pair.Value as int?;
                    }
                }, state);
                if (name is not null) events.Enqueue(new(name, count));
            }
        }
    }
}

[CollectionDefinition(LexfieldContainers.Name)]
public sealed class QueueReconcilerContainers : ICollectionFixture<SqlServerFixture>;
