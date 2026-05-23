using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Conduit.Installer
{
    /// <summary>
    /// Main installer wizard that manages the installation flow
    /// </summary>
    public class InstallerWizard
    {
        private readonly List<IInstallationStep> _steps = new();
        private readonly InstallationContext _context = new();
        private int _currentStepIndex = 0;

        /// <summary>
        /// Adds a step to the installation wizard
        /// </summary>
        /// <param name="step">The installation step</param>
        public void AddStep(IInstallationStep step)
        {
            _steps.Add(step);
        }

        /// <summary>
        /// Runs the installation wizard
        /// </summary>
        public async Task RunAsync()
        {
            Console.Clear();
            DrawHeader();

            while (_currentStepIndex < _steps.Count)
            {
                var step = _steps[_currentStepIndex];
                
                Console.Clear();
                DrawHeader();
                DrawProgress();
                
                var result = await step.ExecuteAsync(_context);
                
                switch (result)
                {
                    case StepResult.Next:
                        _currentStepIndex++;
                        break;
                    case StepResult.Previous:
                        if (_currentStepIndex > 0)
                            _currentStepIndex--;
                        break;
                    case StepResult.Cancel:
                        if (ConfirmCancel())
                            return;
                        break;
                    case StepResult.Error:
                        throw new Exception("Installation step failed");
                }
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

        /// <summary>
        /// Draws the progress indicator
        /// </summary>
        private void DrawProgress()
        {
            Console.Write("Progress: ");
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i < _currentStepIndex)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("█");
                }
                else if (i == _currentStepIndex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("█");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("░");
                }
            }
            Console.ResetColor();
            Console.WriteLine($" ({_currentStepIndex + 1}/{_steps.Count})");
            Console.WriteLine();
        }

        /// <summary>
        /// Confirms cancellation with the user
        /// </summary>
        /// <returns>True if the user confirms cancellation</returns>
        private bool ConfirmCancel()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Are you sure you want to cancel the installation? (Y/N)");
            Console.ResetColor();
            
            var key = Console.ReadKey(true);
            return key.Key == ConsoleKey.Y;
        }
    }

    /// <summary>
    /// Installation step interface
    /// </summary>
    public interface IInstallationStep
    {
        /// <summary>
        /// Gets the step title
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Executes the installation step
        /// </summary>
        /// <param name="context">The installation context</param>
        /// <returns>The step result</returns>
        Task<StepResult> ExecuteAsync(InstallationContext context);
    }

    /// <summary>
    /// Step execution results
    /// </summary>
    public enum StepResult
    {
        /// <summary>
        /// Continue to the next step
        /// </summary>
        Next,

        /// <summary>
        /// Go back to the previous step
        /// </summary>
        Previous,

        /// <summary>
        /// Cancel the installation
        /// </summary>
        Cancel,

        /// <summary>
        /// An error occurred
        /// </summary>
        Error
    }

    /// <summary>
    /// Installation context that holds configuration throughout the installation
    /// </summary>
    public class InstallationContext
    {
        /// <summary>
        /// Gets or sets the installation path
        /// </summary>
        public string InstallPath { get; set; } = @"C:\Program Files\Conduit";

        /// <summary>
        /// Gets or sets the installation type
        /// </summary>
        public InstallationType Type { get; set; } = InstallationType.Full;

        /// <summary>
        /// Gets or sets the database connection string
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether to create the database
        /// </summary>
        public bool CreateDatabase { get; set; } = true;

        /// <summary>
        /// Gets or sets the server port
        /// </summary>
        public int ServerPort { get; set; } = 5000;

        /// <summary>
        /// Gets or sets whether to use HTTPS
        /// </summary>
        public bool UseHttps { get; set; } = true;

        /// <summary>
        /// Gets or sets the admin username
        /// </summary>
        public string AdminUsername { get; set; } = "admin";

        /// <summary>
        /// Gets or sets the admin password
        /// </summary>
        public string AdminPassword { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the JWT secret key
        /// </summary>
        public string JwtSecretKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether to install as a Windows service
        /// </summary>
        public bool InstallAsService { get; set; } = false;

        /// <summary>
        /// Gets or sets the service name
        /// </summary>
        public string ServiceName { get; set; } = "Conduit";

        /// <summary>
        /// Gets or sets whether to start the service after installation
        /// </summary>
        public bool StartService { get; set; } = true;
    }

    /// <summary>
    /// Installation types
    /// </summary>
    public enum InstallationType
    {
        /// <summary>
        /// Full installation with all features
        /// </summary>
        Full,

        /// <summary>
        /// Custom installation with selected features
        /// </summary>
        Custom,

        /// <summary>
        /// Minimal installation
        /// </summary>
        Minimal
    }
}