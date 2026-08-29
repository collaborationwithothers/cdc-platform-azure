using System.Security.Claims;

public sealed class TaskWriteAuthorization
{
    public bool IsAuthorized(ClaimsPrincipal principal, ActorContext actorContext) =>
        actorContext.PermissionMode == "application"
            ? HasRole(principal, "Tasks.Write.All")
            : HasScope(principal, "Tasks.Write");

    private static bool HasScope(ClaimsPrincipal principal, string scope) =>
        principal.FindAll("scp")
            .Concat(principal.FindAll("http://schemas.microsoft.com/identity/claims/scope"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    private static bool HasRole(ClaimsPrincipal principal, string role) =>
        principal.FindAll("roles").Concat(principal.FindAll(ClaimTypes.Role))
            .Any(claim => string.Equals(claim.Value, role, StringComparison.Ordinal));
}
