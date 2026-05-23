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
        private static bool? _setupRequired;

        /// <summary>
        /// Initializes a new instance of the SetupMiddleware class
        /// </summary>
        public SetupMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        /// <summary>
        /// Invokes the middleware
        /// </summary>
        public async Task InvokeAsync(HttpContext context, SetupService setupService)
        {
            // Skip setup check for static files and framework files
            var path = context.Request.Path.Value?.ToLower() ?? "";
            if (path.StartsWith("/_") ||
                path.StartsWith("/css") ||
                path.StartsWith("/js") ||
                path.StartsWith("/lib") ||
                path.StartsWith("/setup") ||
                path.StartsWith("/login") ||
                path.StartsWith("/logout"))
            {
                await _next(context);
                return;
            }

            // Check if setup is required (cache the result)
            if (_setupRequired == null)
            {
                _setupRequired = await setupService.IsSetupRequiredAsync();
            }

            if (_setupRequired.Value)
            {
                // Redirect to setup page
                context.Response.Redirect("/setup");
                return;
            }

            await _next(context);
        }

        /// <summary>
        /// Clears the setup required cache
        /// </summary>
        public static void ClearCache()
        {
            _setupRequired = null;
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