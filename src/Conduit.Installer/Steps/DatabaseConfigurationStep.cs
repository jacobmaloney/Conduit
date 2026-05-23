using System;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Database configuration step
    /// </summary>
    public class DatabaseConfigurationStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Database Configuration";

        /// <inheritdoc/>
        public async Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Database Configuration");
            Console.WriteLine("══════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("Conduit requires a SQL Server database.");
            Console.WriteLine("You can use SQL Server LocalDB, Express, or a full SQL Server instance.");
            Console.WriteLine();
            
            // Default connection string
            if (string.IsNullOrEmpty(context.ConnectionString))
            {
                context.ConnectionString = @"Server=(localdb)\mssqllocaldb;Database=Conduit;Trusted_Connection=True;";
            }
            
            while (true)
            {
                Console.WriteLine("Database Configuration Options:");
                Console.WriteLine("1. Use SQL Server LocalDB (recommended for development)");
                Console.WriteLine("2. Use SQL Server Express");
                Console.WriteLine("3. Use existing SQL Server");
                Console.WriteLine("4. Enter custom connection string");
                Console.WriteLine();
                Console.WriteLine($"Current: {context.ConnectionString}");
                Console.WriteLine();
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Select an option (1-4), T to test connection");
                Console.WriteLine("Press B to go back or ESC to cancel...");
                Console.ResetColor();
                
                var key = Console.ReadKey(true);
                
                switch (key.KeyChar)
                {
                    case '1':
                        context.ConnectionString = @"Server=(localdb)\mssqllocaldb;Database=Conduit;Trusted_Connection=True;";
                        break;
                        
                    case '2':
                        context.ConnectionString = await GetSqlExpressConnectionString();
                        break;
                        
                    case '3':
                        context.ConnectionString = await GetSqlServerConnectionString();
                        break;
                        
                    case '4':
                        context.ConnectionString = await GetCustomConnectionString();
                        break;
                        
                    case 't':
                    case 'T':
                        await TestConnection(context.ConnectionString);
                        break;
                }
                
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        // Test connection before proceeding
                        if (await TestConnection(context.ConnectionString, silent: true))
                        {
                            return StepResult.Next;
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("Connection test failed. Please check your settings.");
                            Console.ResetColor();
                            Console.WriteLine("Press any key to continue...");
                            Console.ReadKey();
                        }
                        break;
                        
                    case ConsoleKey.B:
                        return StepResult.Previous;
                        
                    case ConsoleKey.Escape:
                        return StepResult.Cancel;
                }
                
                Console.Clear();
                DrawHeader();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Database Configuration");
                Console.WriteLine("══════════════════════");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Gets SQL Express connection string
        /// </summary>
        private Task<string> GetSqlExpressConnectionString()
        {
            Console.WriteLine();
            Console.WriteLine("Enter SQL Server Express instance name (default: .\\SQLEXPRESS):");
            Console.Write("> ");
            var instance = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(instance))
                instance = ".\\SQLEXPRESS";

            return Task.FromResult($"Server={instance};Database=Conduit;Trusted_Connection=True;");
        }

        /// <summary>
        /// Gets SQL Server connection string
        /// </summary>
        private Task<string> GetSqlServerConnectionString()
        {
            Console.WriteLine();
            Console.WriteLine("Enter SQL Server name:");
            Console.Write("> ");
            var server = Console.ReadLine() ?? "localhost";

            Console.WriteLine("Use Windows Authentication? (Y/N):");
            var winAuth = Console.ReadKey(true).Key == ConsoleKey.Y;

            if (winAuth)
            {
                return Task.FromResult($"Server={server};Database=Conduit;Trusted_Connection=True;");
            }

            Console.WriteLine("Enter username:");
            Console.Write("> ");
            var username = Console.ReadLine() ?? "sa";

            Console.WriteLine("Enter password:");
            Console.Write("> ");
            var password = ReadPassword();

            return Task.FromResult($"Server={server};Database=Conduit;User Id={username};Password={password};");
        }

        /// <summary>
        /// Gets custom connection string
        /// </summary>
        private Task<string> GetCustomConnectionString()
        {
            Console.WriteLine();
            Console.WriteLine("Enter connection string:");
            Console.Write("> ");
            return Task.FromResult(Console.ReadLine() ?? "");
        }

        /// <summary>
        /// Tests the database connection
        /// </summary>
        private async Task<bool> TestConnection(string connectionString, bool silent = false)
        {
            if (!silent)
            {
                Console.WriteLine();
                Console.Write("Testing connection... ");
            }
            
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                builder.InitialCatalog = "master"; // Test with master first
                
                using (var connection = new SqlConnection(builder.ConnectionString))
                {
                    await connection.OpenAsync();
                }
                
                if (!silent)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Success!");
                    Console.ResetColor();
                    Console.WriteLine("Press any key to continue...");
                    Console.ReadKey();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed!");
                    Console.WriteLine($"Error: {ex.Message}");
                    Console.ResetColor();
                    Console.WriteLine("Press any key to continue...");
                    Console.ReadKey();
                }
                
                return false;
            }
        }

        /// <summary>
        /// Reads a password from console input
        /// </summary>
        private string ReadPassword()
        {
            string password = "";
            ConsoleKeyInfo key;
            
            do
            {
                key = Console.ReadKey(true);
                
                if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
                {
                    password += key.KeyChar;
                    Console.Write("*");
                }
                else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password = password.Substring(0, password.Length - 1);
                    Console.Write("\b \b");
                }
            }
            while (key.Key != ConsoleKey.Enter);
            
            Console.WriteLine();
            return password;
        }

        /// <summary>
        /// Draws the installer header
        /// </summary>
        private void DrawHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                    Conduit Installer                        ║");
            Console.WriteLine("║                        Version 1.0.0                            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
        }
    }
}