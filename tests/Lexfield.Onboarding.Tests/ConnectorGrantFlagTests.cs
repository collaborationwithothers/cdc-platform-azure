using Lexfield.Onboarding;
using Lexfield.TestSupport;

namespace Lexfield.Onboarding.Tests;

[Collection(LexfieldContainers.Name)]
public sealed class ConnectorGrantFlagTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Runner_skips_the_connector_grant_step_and_says_so_when_no_identity_is_supplied()
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

        Assert.Contains(messages, message => message.Contains("skip", StringComparison.OrdinalIgnoreCase));
    }
}
