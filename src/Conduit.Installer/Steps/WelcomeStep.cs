using System;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Welcome step that displays introduction information
    /// </summary>
    public class WelcomeStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Welcome";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Welcome to the Conduit Installation Wizard");
            Console.WriteLine("══════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("This wizard will guide you through the installation of Conduit,");
            Console.WriteLine("a fully-featured SCIM 2.0 server for testing and development.");
            Console.WriteLine();
            
            Console.WriteLine("Conduit includes:");
            Console.WriteLine("  • Full SCIM 2.0 protocol implementation");
            Console.WriteLine("  • Web-based management interface");
            Console.WriteLine("  • User and group management");
            Console.WriteLine("  • Test data generation tools");
            Console.WriteLine("  • API authentication testing");
            Console.WriteLine("  • Custom attribute support");
            Console.WriteLine("  • Audit logging");
            Console.WriteLine();
            
            Console.WriteLine("System Requirements:");
            Console.WriteLine("  • Windows 10/11 or Windows Server 2016+");
            Console.WriteLine("  • .NET 8.0 Runtime");
            Console.WriteLine("  • SQL Server (LocalDB, Express, or full version)");
            Console.WriteLine("  • 100 MB free disk space");
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Press any key to continue or ESC to cancel...");
            Console.ResetColor();
            
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
                return Task.FromResult(StepResult.Cancel);
            
            return Task.FromResult(StepResult.Next);
        }
    }
}