using System;
using System.IO;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// Installation type selection step
    /// </summary>
    public class InstallationTypeStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "Installation Type";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Installation Type");
            Console.WriteLine("═════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("Please select the installation type:");
            Console.WriteLine();
            
            // Display installation types
            var types = new[]
            {
                (InstallationType.Full, "Full Installation", "Install all components including web UI, API, and tools"),
                (InstallationType.Custom, "Custom Installation", "Choose which components to install"),
                (InstallationType.Minimal, "Minimal Installation", "Install only core API components")
            };
            
            int selectedIndex = (int)context.Type;
            
            while (true)
            {
                // Display options
                for (int i = 0; i < types.Length; i++)
                {
                    if (i == selectedIndex)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("► ");
                    }
                    else
                    {
                        Console.Write("  ");
                    }
                    
                    Console.WriteLine($"{types[i].Item2}");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"  {types[i].Item3}");
                    Console.ResetColor();
                    Console.WriteLine();
                }
                
                // Installation path
                Console.WriteLine();
                Console.WriteLine($"Installation Path: {context.InstallPath}");
                Console.WriteLine();
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Use ↑↓ to select, C to change path, Enter to continue");
                Console.WriteLine("Press B to go back or ESC to cancel...");
                Console.ResetColor();
                
                var key = Console.ReadKey(true);
                
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        if (selectedIndex > 0)
                        {
                            selectedIndex--;
                            Console.SetCursorPosition(0, Console.CursorTop - 15);
                            ClearLines(15);
                        }
                        break;
                        
                    case ConsoleKey.DownArrow:
                        if (selectedIndex < types.Length - 1)
                        {
                            selectedIndex++;
                            Console.SetCursorPosition(0, Console.CursorTop - 15);
                            ClearLines(15);
                        }
                        break;
                        
                    case ConsoleKey.C:
                        var newPath = GetInstallationPath(context.InstallPath);
                        if (!string.IsNullOrEmpty(newPath))
                        {
                            context.InstallPath = newPath;
                            Console.SetCursorPosition(0, Console.CursorTop - 15);
                            ClearLines(15);
                        }
                        break;
                        
                    case ConsoleKey.Enter:
                        context.Type = types[selectedIndex].Item1;
                        return Task.FromResult(StepResult.Next);
                        
                    case ConsoleKey.B:
                        return Task.FromResult(StepResult.Previous);
                        
                    case ConsoleKey.Escape:
                        return Task.FromResult(StepResult.Cancel);
                }
            }
        }

        /// <summary>
        /// Gets the installation path from the user
        /// </summary>
        private string GetInstallationPath(string currentPath)
        {
            Console.WriteLine();
            Console.WriteLine("Enter installation path:");
            Console.Write("> ");
            
            var path = Console.ReadLine() ?? currentPath;
            
            if (string.IsNullOrWhiteSpace(path))
                return currentPath;
            
            try
            {
                path = Path.GetFullPath(path);
                
                // Validate path
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Invalid path. Press any key to continue...");
                    Console.ResetColor();
                    Console.ReadKey();
                    return currentPath;
                }
                
                return path;
            }
            catch
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Invalid path. Press any key to continue...");
                Console.ResetColor();
                Console.ReadKey();
                return currentPath;
            }
        }

        /// <summary>
        /// Clears the specified number of lines
        /// </summary>
        private void ClearLines(int lines)
        {
            for (int i = 0; i < lines; i++)
            {
                Console.WriteLine(new string(' ', Console.WindowWidth));
            }
            Console.SetCursorPosition(0, Console.CursorTop - lines);
        }
    }
}