using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Server configuration step
    /// </summary>
    public class ServerConfigurationStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Server Configuration";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Server Configuration");
            Console.WriteLine("════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            while (true)
            {
                Console.WriteLine("Configure how the Conduit will run:");
                Console.WriteLine();
                
                // Server port
                Console.WriteLine($"1. Server Port: {context.ServerPort}");
                Console.WriteLine($"2. Use HTTPS: {(context.UseHttps ? "Yes" : "No")}");
                Console.WriteLine($"3. Install as Windows Service: {(context.InstallAsService ? "Yes" : "No")}");
                
                if (context.InstallAsService)
                {
                    Console.WriteLine($"4. Service Name: {context.ServiceName}");
                    Console.WriteLine($"5. Start Service After Installation: {(context.StartService ? "Yes" : "No")}");
                }
                
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Select an option to modify (1-5), or press Enter to continue");
                Console.WriteLine("Press B to go back or ESC to cancel...");
                Console.ResetColor();
                
                var key = Console.ReadKey(true);
                
                switch (key.KeyChar)
                {
                    case '1':
                        context.ServerPort = GetPort(context.ServerPort);
                        break;
                        
                    case '2':
                        context.UseHttps = !context.UseHttps;
                        break;
                        
                    case '3':
                        context.InstallAsService = !context.InstallAsService;
                        break;
                        
                    case '4':
                        if (context.InstallAsService)
                            context.ServiceName = GetServiceName(context.ServiceName);
                        break;
                        
                    case '5':
                        if (context.InstallAsService)
                            context.StartService = !context.StartService;
                        break;
                }
                
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        // Validate port availability
                        if (IsPortAvailable(context.ServerPort))
                        {
                            return Task.FromResult(StepResult.Next);
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Port {context.ServerPort} is already in use. Please choose a different port.");
                            Console.ResetColor();
                            Console.WriteLine("Press any key to continue...");
                            Console.ReadKey();
                        }
                        break;
                        
                    case ConsoleKey.B:
                        return Task.FromResult(StepResult.Previous);
                        
                    case ConsoleKey.Escape:
                        return Task.FromResult(StepResult.Cancel);
                }
                
                Console.Clear();
                DrawHeader();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Server Configuration");
                Console.WriteLine("════════════════════");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Gets port number from user
        /// </summary>
        private int GetPort(int currentPort)
        {
            Console.WriteLine();
            Console.WriteLine($"Enter port number (current: {currentPort}):");
            Console.Write("> ");
            
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                return currentPort;
            
            if (int.TryParse(input, out var port) && port > 0 && port <= 65535)
            {
                return port;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Invalid port number. Press any key to continue...");
                Console.ResetColor();
                Console.ReadKey();
                return currentPort;
            }
        }

        /// <summary>
        /// Gets service name from user
        /// </summary>
        private string GetServiceName(string currentName)
        {
            Console.WriteLine();
            Console.WriteLine($"Enter service name (current: {currentName}):");
            Console.Write("> ");
            
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                return currentName;
            
            // Validate service name
            if (input.IndexOfAny(new[] { '/', '\\', ' ' }) >= 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Service name cannot contain spaces or slashes. Press any key to continue...");
                Console.ResetColor();
                Console.ReadKey();
                return currentName;
            }
            
            return input;
        }

        /// <summary>
        /// Checks if a port is available
        /// </summary>
        private bool IsPortAvailable(int port)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                    return true;
                }
            }
            catch
            {
                return false;
            }
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