using System.Security.Claims;
using Microsoft.Extensions.Logging;

public sealed class ActorContextResolver(ILogger<ActorContextResolver> logger)
{
    public ActorContext? Resolve(ClaimsPrincipal principal)
    {
        // .NET can map tid and oid to Microsoft URI claim types. idtyp has no URI mapping.
        var tenantId = FindValue(principal, "tid", "http://schemas.microsoft.com/identity/claims/tenantid");
        var objectId = FindValue(principal, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
        var identityType = FindValue(principal, "idtyp");
        var missingClaim = string.IsNullOrWhiteSpace(tenantId)
            ? "tid"
            : string.IsNullOrWhiteSpace(objectId)
                ? "oid"
                : string.IsNullOrWhiteSpace(identityType)
                    ? "idtyp"
                    : null;
        if (missingClaim is not null)
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = "TaskApi.WriteIdentityRejected"
            }))
            {
                logger.LogWarning(
                    "Task API rejected a write because the validated token is missing required identity claim '{MissingClaim}'.",
                    missingClaim);
            }
            return null;
        }

        var application = string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase);
        var permissionMode = application
            ? ActorPermissionMode.Application
            : ActorPermissionMode.Delegated;
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
    string Actor, string? ClientApplicationId, ActorPermissionMode PermissionMode)
{
    public string PermissionModeValue => PermissionMode switch
    {
        ActorPermissionMode.Application => "application",
        ActorPermissionMode.Delegated => "delegated",
        _ => throw new InvalidOperationException($"Unknown actor permission mode {PermissionMode}.")
    };
}

public enum ActorPermissionMode { Delegated, Application }
