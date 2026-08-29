using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

public static class TaskApiAuthentication
{
    public static IServiceCollection AddTaskApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<ActorContextResolver>();
        services.AddSingleton<TaskWriteAuthorization>();
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
        services.AddAuthorization(options => options.AddPolicy("TenantRoute", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new TenantRouteRequirement());
        }));
        services.AddSingleton<IAuthorizationHandler, TenantRouteHandler>();
        return services;
    }
}

public sealed class TenantRouteRequirement : IAuthorizationRequirement;
public sealed class TenantRouteHandler : AuthorizationHandler<TenantRouteRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantRouteRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && http.Request.RouteValues.TryGetValue("tenantId", out var routeTenant)
            && string.Equals(routeTenant?.ToString(), context.User.FindFirstValue("tenantId"),
                StringComparison.Ordinal)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
