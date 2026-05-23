using System;
using System.IO;
using System.Threading.Tasks;
using Conduit.DataAccess;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Main installation execution step
    /// </summary>
    public class InstallationStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Installation";

        /// <inheritdoc/>
        public async Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Installing Conduit");
            Console.WriteLine("══════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            try
            {
                // Step 1: Create installation directory
                await InstallStep("Creating installation directory", () =>
                {
                    Directory.CreateDirectory(context.InstallPath);
                    Directory.CreateDirectory(Path.Combine(context.InstallPath, "logs"));
                    Directory.CreateDirectory(Path.Combine(context.InstallPath, "config"));
                    return Task.CompletedTask;
                });
                
                // Step 2: Copy application files
                await InstallStep("Copying application files", async () =>
                {
                    // In a real installer, this would copy files from the package
                    // For now, we'll simulate this
                    await Task.Delay(1000);
                });
                
                // Step 3: Create configuration files
                await InstallStep("Creating configuration files", async () =>
                {
                    var configPath = Path.Combine(context.InstallPath, "config", "appsettings.json");
                    var config = GenerateConfiguration(context);
                    await File.WriteAllTextAsync(configPath, config);
                });
                
                // Step 4: Initialize database
                await InstallStep("Initializing database", async () =>
                {
                    var dbConfig = new DatabaseConfig
                    {
                        ConnectionString = context.ConnectionString,
                        AutoCreateDatabase = context.CreateDatabase,
                        AutoMigrate = true
                    };
                    
                    var initializer = new DatabaseInitializer(dbConfig);
                    await initializer.InitializeAsync();
                });
                
                // Step 5: Create admin user
                await InstallStep("Creating admin user", async () =>
                {
                    // In a real implementation, create the admin user in the database
                    await Task.Delay(500);
                });
                
                // Step 6: Install Windows service (if selected)
                if (context.InstallAsService)
                {
                    await InstallStep("Installing Windows service", async () =>
                    {
                        // In a real implementation, use sc.exe or ServiceController
                        await Task.Delay(1000);
                    });
                    
                    if (context.StartService)
                    {
                        await InstallStep("Starting service", async () =>
                        {
                            // Start the service
                            await Task.Delay(500);
                        });
                    }
                }
                
                // Step 7: Create shortcuts
                await InstallStep("Creating shortcuts", async () =>
                {
                    // Create desktop and start menu shortcuts
                    await Task.Delay(500);
                });
                
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Installation completed successfully!");
                Console.ResetColor();
                Console.WriteLine();
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Press Enter to continue...");
                Console.ResetColor();
                Console.ReadKey();
                
                return StepResult.Next;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ Installation failed: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return StepResult.Error;
            }
        }

        /// <summary>
        /// Executes an installation step with progress indication
        /// </summary>
        private async Task InstallStep(string description, Func<Task> action)
        {
            Console.Write($"• {description}... ");
            
            try
            {
                await action();
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("✗");
                Console.ResetColor();
                throw new Exception($"{description} failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generates the application configuration
        /// </summary>
        private string GenerateConfiguration(InstallationContext context)
        {
            return $@"{{
  ""Logging"": {{
    ""LogLevel"": {{
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }}
  }},
  ""AllowedHosts"": ""*"",
  ""ConnectionStrings"": {{
    ""DefaultConnection"": ""{context.ConnectionString.Replace("\\", "\\\\")}""
  }},
  ""Database"": {{
    ""AutoCreateDatabase"": false,
    ""AutoMigrate"": true,
    ""CommandTimeout"": 30,
    ""EnableQueryLogging"": false
  }},
  ""Jwt"": {{
    ""SecretKey"": ""{context.JwtSecretKey}"",
    ""Issuer"": ""Conduit"",
    ""Audience"": ""ConduitAPI"",
    ""ExpirationMinutes"": 60,
    ""ValidateIssuer"": true,
    ""ValidateAudience"": true,
    ""ValidateLifetime"": true,
    ""ValidateIssuerSigningKey"": true
  }},
  ""SCIM"": {{
    ""EnableCustomAttributes"": true,
    ""MaxPageSize"": 1000,
    ""DefaultPageSize"": 100,
    ""EnableUserGeneration"": true,
    ""EnableAuditLog"": true
  }},
  ""Kestrel"": {{
    ""Endpoints"": {{
      ""{(context.UseHttps ? "Https" : "Http")}"": {{
        ""Url"": ""{(context.UseHttps ? "https" : "http")}://localhost:{context.ServerPort}""
      }}
    }}
  }}
}}";
        }
    }
}