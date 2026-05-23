using System;
using System.Threading.Tasks;

namespace Conduit.Installer.Steps
{
    /// <summary>
    /// License agreement step
    /// </summary>
    public class LicenseStep : IInstallationStep
    {
        /// <inheritdoc/>
        public string Title => "License Agreement";

        /// <inheritdoc/>
        public Task<StepResult> ExecuteAsync(InstallationContext context)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("License Agreement");
            Console.WriteLine("═════════════════");
            Console.ResetColor();
            Console.WriteLine();
            
            Console.WriteLine("MIT License");
            Console.WriteLine();
            Console.WriteLine("Copyright (c) 2024 Conduit Contributors");
            Console.WriteLine();
            Console.WriteLine("Permission is hereby granted, free of charge, to any person obtaining a copy");
            Console.WriteLine("of this software and associated documentation files (the \"Software\"), to deal");
            Console.WriteLine("in the Software without restriction, including without limitation the rights");
            Console.WriteLine("to use, copy, modify, merge, publish, distribute, sublicense, and/or sell");
            Console.WriteLine("copies of the Software, and to permit persons to whom the Software is");
            Console.WriteLine("furnished to do so, subject to the following conditions:");
            Console.WriteLine();
            Console.WriteLine("The above copyright notice and this permission notice shall be included in all");
            Console.WriteLine("copies or substantial portions of the Software.");
            Console.WriteLine();
            Console.WriteLine("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR");
            Console.WriteLine("IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,");
            Console.WriteLine("FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE");
            Console.WriteLine("AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER");
            Console.WriteLine("LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,");
            Console.WriteLine("OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE");
            Console.WriteLine("SOFTWARE.");
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Do you accept the license agreement? (Y/N)");
            Console.WriteLine("Press B to go back or ESC to cancel...");
            Console.ResetColor();
            
            while (true)
            {
                var key = Console.ReadKey(true);
                
                switch (key.Key)
                {
                    case ConsoleKey.Y:
                        return Task.FromResult(StepResult.Next);
                    case ConsoleKey.N:
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("You must accept the license agreement to continue.");
                        Console.ResetColor();
                        Console.WriteLine("Press any key to exit...");
                        Console.ReadKey();
                        return Task.FromResult(StepResult.Cancel);
                    case ConsoleKey.B:
                        return Task.FromResult(StepResult.Previous);
                    case ConsoleKey.Escape:
                        return Task.FromResult(StepResult.Cancel);
                }
            }
        }
    }
}