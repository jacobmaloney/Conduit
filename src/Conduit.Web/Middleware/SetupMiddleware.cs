using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Web.Services;
using System.Threading.Tasks;

namespace Conduit.Web.Middleware
{
    /// <summary>
    /// Middleware to redirect to setup page if initial configuration is required
    /// </summary>
    public class SetupMiddleware
    {
        private readonly RequestDelegate _next;

        /// <summary>
        /// Initializes a new instance of the SetupMiddleware class
        /// </summary>
        public SetupMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        /// <summary>
        /// Invokes the middleware. Routes by the three-state database status:
        ///   Unreachable → /db-offline (NEVER the wizard — a network blip must not expose
        ///   first-run setup), NotConfigured → /setup (legitimate first run), Ready → normal.
        /// De-hammering / brief caching lives in SetupService.GetDatabaseStatusAsync so a
        /// transient outage can recover without a latched middleware flag.
        /// </summary>
        public async Task InvokeAsync(HttpContext context, SetupService setupService)
        {
            // Skip setup check for static files, framework files, and the anonymous
            // bootstrap routes (including the offline page itself, so it can render even
            // while the DB is down).
            var path = context.Request.Path.Value?.ToLower() ?? "";
            if (path.StartsWith("/_") ||
                path.StartsWith("/css") ||
                path.StartsWith("/js") ||
                path.StartsWith("/lib") ||
                path.StartsWith("/setup") ||
                path.StartsWith("/db-offline") ||
                path.StartsWith("/login") ||
                path.StartsWith("/logout"))
            {
                await _next(context);
                return;
            }

            var status = await setupService.GetDatabaseStatusAsync();

            switch (status)
            {
                case DatabaseStatus.Unreachable:
                    // Transient/infra-down: show a clear retrying page, NOT the wizard.
                    context.Response.Redirect("/db-offline");
                    return;
                case DatabaseStatus.NotConfigured:
                    // Genuine first run.
                    context.Response.Redirect("/setup");
                    return;
                default:
                    await _next(context);
                    return;
            }
        }

        /// <summary>
        /// Clears the cached database status (invoked after setup completes so the next
        /// request re-evaluates immediately rather than waiting for the cache TTL).
        /// </summary>
        public static void ClearCache()
        {
            SetupService.ClearStatusCache();
        }
    }

    /// <summary>
    /// Extension methods for SetupMiddleware
    /// </summary>
    public static class SetupMiddlewareExtensions
    {
        /// <summary>
        /// Adds the setup middleware to the pipeline
        /// </summary>
        public static IApplicationBuilder UseSetupCheck(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<SetupMiddleware>();
        }
    }
}