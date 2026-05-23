using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Conduit.Web.Services;
using Conduit.DataAccess.Repositories;
using Conduit.Core.Models;

namespace Conduit.Web.Middleware;

/// <summary>
/// Resolves the active Connected System from two possible sources and writes
/// the result into <see cref="HttpContext.Items"/> so downstream services
/// (TenantContext + repositories) see the correct tenant:
///
///  1. URL slug — when the route matches /scim/v2/t/{slug}/... the slug is
///     authoritative. A 404 is returned if the slug doesn't match an active tenant.
///  2. Bearer scim_ token — the legacy unscoped /scim/v2/... route still works;
///     the tenant comes from the token's TenantId.
///
/// When both are present they must agree, otherwise the request is rejected
/// with 403 — this is the whole point of the URL-slug routing: a leaked token
/// cannot be aimed at a different connection just by changing the URL.
/// </summary>
public class ApiTokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Slug from URL route, if any.
        var slug = context.GetRouteValue("slug") as string;
        Tenant? slugTenant = null;
        if (!string.IsNullOrEmpty(slug))
        {
            using var scope = context.RequestServices.CreateScope();
            var tenantRepo = scope.ServiceProvider.GetRequiredService<TenantRepository>();
            slugTenant = await tenantRepo.GetBySlugAsync(slug);
            if (slugTenant == null || !slugTenant.IsActive)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"Connected System '{slug}' not found.");
                return;
            }
        }

        // 2. Bearer scim_ token, if any.
        var authHeader = context.Request.Headers["Authorization"].ToString();
        Conduit.Web.Services.ApiToken? apiToken = null;
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer scim_", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();

            using var scope = context.RequestServices.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            apiToken = await tokenService.ValidateTokenAsync(token);

            if (apiToken == null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
                return;
            }

            // Cross-check token vs slug. A token scoped to one tenant cannot
            // be used against a different tenant's URL.
            if (slugTenant != null && apiToken.TenantId is { } tokenTid && tokenTid != slugTenant.Id)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers["WWW-Authenticate"] =
                    "Bearer error=\"insufficient_scope\", error_description=\"Token is not scoped to this Connected System.\"";
                return;
            }
        }

        // 3. Resolve the effective tenant. URL slug wins when present.
        Guid? effectiveTenantId = slugTenant?.Id ?? apiToken?.TenantId;
        string effectiveScope = slugTenant != null
            ? "Tenant"
            : (apiToken?.Scope ?? (apiToken == null ? "" : "Tenant"));

        if (effectiveTenantId.HasValue)
        {
            context.Items[TenantContext.TenantIdItemKey] = effectiveTenantId.Value;
        }
        if (!string.IsNullOrEmpty(effectiveScope))
        {
            context.Items[TenantContext.ScopeItemKey] = effectiveScope;
        }

        // 4. If the token was supplied, attach the claims principal so [Authorize] passes.
        if (apiToken != null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, apiToken.Id.ToString()),
                new(ClaimTypes.Name, apiToken.Name),
                new("TokenType", "ApiToken"),
                new("TokenScope", apiToken.Scope ?? "Tenant"),
                new("scope", "scim:read"),
                new("scope", "scim:write")
            };
            if (effectiveTenantId is { } tid)
            {
                claims.Add(new Claim("TenantId", tid.ToString()));
            }

            var identity = new ClaimsIdentity(claims, "ApiToken");
            context.User = new ClaimsPrincipal(identity);
        }

        await _next(context);
    }
}
