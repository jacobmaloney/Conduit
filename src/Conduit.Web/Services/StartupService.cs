using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Hosted service that runs startup checks
    /// </summary>
    public class StartupService : IHostedService
    {
        private readonly ILogger<StartupService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHostApplicationLifetime _lifetime;

        /// <summary>
        /// Initializes a new instance of the StartupService class
        /// </summary>
        public StartupService(
            ILogger<StartupService> logger,
            IServiceProvider serviceProvider,
            IHostApplicationLifetime lifetime)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _lifetime = lifetime;
        }

        /// <summary>
        /// Starts the service
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Conduit starting...");

            try
            {
                // Check if setup is required
                using var scope = _serviceProvider.CreateScope();
                var setupService = scope.ServiceProvider.GetRequiredService<SetupService>();
                var setupRequired = await setupService.IsSetupRequiredAsync();

                if (setupRequired)
                {
                    _logger.LogWarning("Initial setup required. Please navigate to /setup to configure Conduit.");
                    Console.WriteLine();
                    Console.WriteLine("========================================");
                    Console.WriteLine("  Conduit - Initial Setup Required");
                    Console.WriteLine("========================================");
                    Console.WriteLine();
                    Console.WriteLine("This appears to be the first time running Conduit.");
                    Console.WriteLine("Please open your web browser and navigate to:");
                    Console.WriteLine();
                    Console.WriteLine("  http://localhost:5000/setup");
                    Console.WriteLine();
                    Console.WriteLine("to complete the initial configuration.");
                    Console.WriteLine();
                }
                else
                {
                    _logger.LogInformation("Conduit is ready!");
                    Console.WriteLine();
                    Console.WriteLine("========================================");
                    Console.WriteLine("  Conduit Started Successfully");
                    Console.WriteLine("========================================");
                    Console.WriteLine();
                    Console.WriteLine("Web UI: http://localhost:5000");
                    Console.WriteLine("API Base: http://localhost:5000/scim/v2");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during startup checks, setup may be required");
                // Don't stop — let SetupMiddleware redirect to /setup
            }
        }

        /// <summary>
        /// Stops the service
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Conduit stopped.");
            return Task.CompletedTask;
        }
    }
}