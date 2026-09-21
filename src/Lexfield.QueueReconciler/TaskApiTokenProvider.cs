namespace Lexfield.QueueReconciler;

internal sealed class TaskApiTokenProvider(IReadOnlyDictionary<string, string> tokens)
{
    public string ForTenant(string tenantId) => tokens.TryGetValue(tenantId, out var token)
        ? token
        : throw new InvalidOperationException(
            $"No task-api bearer token is configured for tenant '{tenantId}'.");
}
