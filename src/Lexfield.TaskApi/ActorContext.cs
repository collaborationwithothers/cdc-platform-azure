using System.Security.Claims;

public sealed class ActorContextResolver
{
    public ActorContext? Resolve(ClaimsPrincipal principal)
    {
        var tenantId = FindValue(principal, "tid", "http://schemas.microsoft.com/identity/claims/tenantid");
        var objectId = FindValue(principal, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
        var identityType = FindValue(principal, "idtyp");
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(objectId)
            || string.IsNullOrWhiteSpace(identityType)) return null;

        var application = string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase);
        var permissionMode = application ? "application" : "delegated";
        var actorType = application ? "workload" : "user";
        var clientApplicationId = FindValue(principal, "azp")
            ?? FindValue(principal, "appid");
        return new ActorContext(
            $"{actorType}:{tenantId}:{objectId}", clientApplicationId, permissionMode);
    }

    private static string? FindValue(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes.Select(claimType => principal.FindFirst(claimType)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record ActorContext(
    string Actor, string? ClientApplicationId, string PermissionMode);
