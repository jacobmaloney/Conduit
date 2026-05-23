using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Installation completion step
    /// </summary>
    public class CompletionStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Installation Complete";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Installation Complete");
            Console.WriteLine("════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Conduit has been successfully installed!");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("Installation Summary:");
            Console.WriteLine($"  • Location: {context.InstallPath}");
            Console.WriteLine($"  • Type: {context.Type}");
            Console.WriteLine($"  • Database: Connected");
            Console.WriteLine($"  • Server URL: {(context.UseHttps ? "https" : "http")}://localhost:{context.ServerPort}");
            
            if (context.InstallAsService)
            {
                Console.WriteLine($"  • Service: {context.ServiceName} ({(context.StartService ? "Running" : "Stopped")})");
            }
            
            Console.WriteLine();
            Console.WriteLine("Next Steps:");
            Console.WriteLine($"  1. Access the web interface at {(context.UseHttps ? "https" : "http")}://localhost:{context.ServerPort}");
            Console.WriteLine($"  2. Log in with username: {context.AdminUsername}");
            Console.WriteLine("  3. Configure your SCIM clients to connect to the API endpoints");
            Console.WriteLine("  4. Generate test data using the built-in tools");
            Console.WriteLine();
            
            Console.WriteLine("Important Files:");
            Console.WriteLine($"  • Configuration: {context.InstallPath}\\config\\appsettings.json");
            Console.WriteLine($"  • Logs: {context.InstallPath}\\logs\\");
            Console.WriteLine();
            
            Console.WriteLine("Documentation:");
            Console.WriteLine("  • API Documentation: /swagger");
            Console.WriteLine("  • GitHub: https://github.com/yourusername/conduit");
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Would you like to:");
            Console.WriteLine("  L - Launch Conduit now");
            Console.WriteLine("  D - Open installation directory");
            Console.WriteLine("  F - Finish and exit");
            Console.ResetColor();
            
            while (true)
            {
                var key = Console.ReadKey(true);
                
                switch (key.Key)
                {
                    case ConsoleKey.L:
                        LaunchApplication(context);
                        return Task.FromResult(StepResult.Next);
                        
                    case ConsoleKey.D:
                        OpenDirectory(context.InstallPath);
                        break;
                        
                    case ConsoleKey.F:
                    case ConsoleKey.Enter:
                    case ConsoleKey.Escape:
                        return Task.FromResult(StepResult.Next);
                }
            }
        }

        /// <summary>
        /// Launches the Conduit application
        /// </summary>
        private void LaunchApplication(InstallationContext context)
        {
            try
            {
                var url = $"{(context.UseHttps ? "https" : "http")}://localhost:{context.ServerPort}";
                
                if (context.InstallAsService && context.StartService)
                {
                    // Service is already running, just open the browser
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Start the application
                    var exePath = System.IO.Path.Combine(context.InstallPath, "Conduit.Web.exe");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = context.InstallPath,
                        UseShellExecute = true
                    });
                    
                    // Wait a moment for the server to start
                    System.Threading.Thread.Sleep(3000);
                    
                    // Open browser
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to launch application: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }
        }

        /// <summary>
        /// Opens the installation directory in Explorer
        /// </summary>
        private void OpenDirectory(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to open directory: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }
        }
    }
}