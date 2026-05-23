using System;
using Microsoft.AspNetCore.Http;
using Conduit.Core.Models;
using Conduit.Core.Services;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Default <see cref="ITenantContext"/> implementation backed by <see cref="HttpContext.Items"/>.
    /// Populated by <see cref="Middleware.ApiTokenAuthMiddleware"/> from the validated API token.
    /// </summary>
    public class TenantContext : ITenantContext
    {
        public const string TenantIdItemKey = "TenantId";
        public const string ScopeItemKey = "TokenScope";

        private readonly IHttpContextAccessor _accessor;

        public TenantContext(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        private HttpContext? Ctx => _accessor.HttpContext;

        public bool IsResolved => Ctx?.Items.ContainsKey(ScopeItemKey) == true;

        public Guid? TenantId
        {
            get
            {
                var v = Ctx?.Items[TenantIdItemKey];
                return v is Guid g ? g : (Guid?)null;
            }
        }

        public ApiTokenScope Scope
        {
            get
            {
                var v = Ctx?.Items[ScopeItemKey] as string;
                return Enum.TryParse<ApiTokenScope>(v, ignoreCase: true, out var parsed)
                    ? parsed
                    : ApiTokenScope.Tenant;
            }
        }

        public bool IsAdmin => Scope == ApiTokenScope.Admin;
        public bool IsArsProxy => Scope == ApiTokenScope.ArsProxy;

        public Guid RequireTenantId()
        {
            if (TenantId is { } id) return id;
            throw new InvalidOperationException(
                "No tenant in scope. The caller has Admin scope; you must accept an explicit tenant from the request " +
                "or refuse the operation.");
        }
    }
}
