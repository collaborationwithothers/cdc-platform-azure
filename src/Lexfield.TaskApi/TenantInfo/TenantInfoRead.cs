using Dapper;
using Microsoft.Data.SqlClient;

namespace Lexfield.TaskApi.TenantInfo;

/// <summary>
/// Reads the tenantId a tenant's own database claims for itself, written into
/// dbo.TenantInfo once at onboarding. The isolation check compares this claim,
/// written by the onboarding runner, against the tenantId a connector stamps
/// onto every message, written from connector configuration. Two facts written
/// independently, so a mistake in one shows up as a disagreement rather than
/// staying invisible. This read exposes the database side of that comparison.
/// </summary>
public sealed class TenantInfoRead(TenantCatalog catalog)
{
    /// <summary>
    /// Returns the claim, or null when the tenant is unknown to the catalog. The
    /// single-row table holds Id = 1, so the read is keyed on it directly.
    /// </summary>
    public async Task<TenantInfoClaim?> ReadAsync(string tenantId, CancellationToken cancellationToken)
    {
        var connectionString = catalog.GetConnectionString(tenantId);
        if (connectionString is null) return null;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TenantInfoClaim>(new CommandDefinition(
            "SELECT TenantId, ClaimedAt FROM dbo.TenantInfo WHERE Id = 1",
            cancellationToken: cancellationToken));
    }
}

public sealed record TenantInfoClaim(string TenantId, DateTime ClaimedAt);
