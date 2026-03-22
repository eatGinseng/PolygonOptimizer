using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FileLoadDemo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private void App_Startup(object sender, StartupEventArgs e)
        {
            string? filePath = null;

            if (e.Args.Length > 0)
            {
                filePath = e.Args[0];

                if (!File.Exists(filePath))
                {
                    AttachConsole(-1); // Attach to parent console
                    Console.Error.WriteLine($"Error: File not found: {filePath}");
                    Shutdown(1);
                    return;
                }
            }

            var window = new MainWindow(filePath);
            window.Show();
        }
    }
}
