using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Security configuration step
    /// </summary>
    public class SecurityConfigurationStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Security Configuration";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Security Configuration");
            Console.WriteLine("══════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("Configure security settings for Conduit:");
            Console.WriteLine();
            
            // Admin credentials
            bool validCredentials = false;
            while (!validCredentials)
            {
                // Admin username
                Console.WriteLine($"Admin Username [{context.AdminUsername}]:");
                Console.Write("> ");
                var username = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(username))
                    context.AdminUsername = username;
                
                // Admin password
                Console.WriteLine("Admin Password (required):");
                Console.Write("> ");
                context.AdminPassword = ReadPassword();
                
                if (string.IsNullOrWhiteSpace(context.AdminPassword))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Password is required.");
                    Console.ResetColor();
                    continue;
                }
                
                // Confirm password
                Console.WriteLine("Confirm Password:");
                Console.Write("> ");
                var confirmPassword = ReadPassword();
                
                if (context.AdminPassword != confirmPassword)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Passwords do not match. Please try again.");
                    Console.ResetColor();
                    continue;
                }
                
                // Validate password strength
                if (context.AdminPassword.Length < 8)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Password must be at least 8 characters long.");
                    Console.ResetColor();
                    continue;
                }
                
                validCredentials = true;
            }
            
            // Generate JWT secret key
            Console.WriteLine();
            Console.WriteLine("Generating JWT secret key...");
            context.JwtSecretKey = GenerateSecretKey();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ JWT secret key generated");
            Console.ResetColor();
            
            // Security recommendations
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Security Recommendations:");
            Console.ResetColor();
            Console.WriteLine("• Change the default JWT secret key in production");
            Console.WriteLine("• Use HTTPS in production environments");
            Console.WriteLine("• Regularly rotate API tokens");
            Console.WriteLine("• Enable audit logging for compliance");
            Console.WriteLine("• Restrict database access to the application only");
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Press Enter to continue, B to go back, or ESC to cancel...");
            Console.ResetColor();
            
            while (true)
            {
                var key = Console.ReadKey(true);
                
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return Task.FromResult(StepResult.Next);
                        
                    case ConsoleKey.B:
                        return Task.FromResult(StepResult.Previous);
                        
                    case ConsoleKey.Escape:
                        return Task.FromResult(StepResult.Cancel);
                }
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
        /// Generates a secure random secret key
        /// </summary>
        private string GenerateSecretKey()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[64]; // 512 bits
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes);
            }
        }
    }
}