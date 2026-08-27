using Lexfield.Onboarding;
using Lexfield.TestSupport;

namespace Lexfield.Onboarding.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class ConnectorGrantFlagTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Onboarding_explains_that_connector_access_was_skipped_when_identity_is_omitted()
    {
        const string tenantId = "lexfield-002";
        const string databaseName = "onboarding_flag_off";
        await sql.CreateEmptyTenantDatabaseAsync(databaseName);
        var runner = new TenantOnboardingRunner(entry => sql.ConnectionStringFor(entry.Database));
        var messages = new List<string>();

        await runner.RunAsync(
            [new TenantManifestEntry(tenantId, databaseName, StreamIsolated: false)],
            connectorIdentity: null,
            log: messages.Add);

        Assert.True(
            messages.Any(message => message.Contains("connector grant skipped", StringComparison.OrdinalIgnoreCase)),
            "Onboarding should explain that connector access was skipped when no connector identity was supplied.");
        Assert.Contains(
            messages,
            message => message.Contains(tenantId, StringComparison.Ordinal));
    }
}
