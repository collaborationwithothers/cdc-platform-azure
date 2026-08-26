namespace Lexfield.TaskApi.TenantInfo;

public static class TenantInfoEndpoints
{
    public static IEndpointRouteBuilder MapTenantInfoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{tenantId}/info", async (
            string tenantId, TenantCatalog catalog, CancellationToken cancellationToken) =>
        {
            var claim = await new TenantInfoRead(catalog).ReadAsync(tenantId, cancellationToken);
            return claim is null ? Results.NotFound() : Results.Ok(claim);
        }).RequireAuthorization("TenantRoute");
        return endpoints;
    }
}
