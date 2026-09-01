using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

public static class TaskApiAuthentication
{
    public const string TenantRoutePolicy = "TenantRoute";
    public const string TaskWritePolicy = "TaskWrite";

    public static IServiceCollection AddTaskApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<ActorContextResolver>();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Authority = configuration["Authentication:Authority"];
                options.Audience = configuration["Authentication:Audience"];
                options.RequireHttpsMetadata = true;
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.Authority),
                "Task API cannot start because Authentication:Authority (the token issuer URL) is missing. Set it in appsettings.json or through the Authentication__Authority environment variable.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience),
                "Task API cannot start because Authentication:Audience (the identifier for this API) is missing. Set it in appsettings.json or through the Authentication__Audience environment variable.")
            .ValidateOnStart();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TenantRoutePolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new TenantRouteRequirement());
            });
            options.AddPolicy(TaskWritePolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new TenantRouteRequirement(), new TaskWritePermissionRequirement());
            });
        });
        services.AddSingleton<IAuthorizationHandler, TenantRouteHandler>();
        services.AddSingleton<IAuthorizationHandler, TaskWritePermissionHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, TaskApiAuthorizationMiddlewareResultHandler>();
        return services;
    }
}

public sealed class TenantRouteRequirement : IAuthorizationRequirement;

public sealed class TaskWritePermissionRequirement : IAuthorizationRequirement;

internal static class TaskApiAuthorizationState
{
    public static readonly object ActorContext = new();
    public static readonly object InvalidActorContext = new();
    public static readonly object TenantRouteFailed = new();
}

public sealed class TaskWritePermissionHandler(ActorContextResolver actorContexts)
    : AuthorizationHandler<TaskWritePermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TaskWritePermissionRequirement requirement)
    {
        var actorContext = actorContexts.Resolve(context.User);
        if (actorContext is null)
        {
            if (context.Resource is HttpContext http)
                http.Items[TaskApiAuthorizationState.InvalidActorContext] = true;
            return Task.CompletedTask;
        }

        if (context.Resource is HttpContext validHttp)
            validHttp.Items[TaskApiAuthorizationState.ActorContext] = actorContext;
        if (TaskWriteAuthorization.IsAuthorized(context.User, actorContext)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class TaskApiAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && policy.Requirements.OfType<TaskWritePermissionRequirement>().Any()
            && context.Items.ContainsKey(TaskApiAuthorizationState.InvalidActorContext)
            && !context.Items.ContainsKey(TaskApiAuthorizationState.TenantRouteFailed))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class TaskWriteAuthorization
{
    public static bool IsAuthorized(ClaimsPrincipal principal, ActorContext actorContext) =>
        actorContext.PermissionMode == ActorPermissionMode.Application
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

public sealed class TenantRouteHandler : AuthorizationHandler<TenantRouteRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantRouteRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && http.Request.RouteValues.TryGetValue("tenantId", out var routeTenant)
            && string.Equals(routeTenant?.ToString(), context.User.FindFirstValue("tenantId"),
                StringComparison.Ordinal)) context.Succeed(requirement);
        else if (context.Resource is HttpContext failedHttp)
            failedHttp.Items[TaskApiAuthorizationState.TenantRouteFailed] = true;
        return Task.CompletedTask;
    }
}
