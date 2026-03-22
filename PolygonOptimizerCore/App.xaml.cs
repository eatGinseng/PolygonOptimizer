using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            bool batchMode = e.Args.Contains("--batch");
            string? folder = null;
            var rawPaths = new List<string>();

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--batch") continue;
                if (e.Args[i] == "--folder" && i + 1 < e.Args.Length)
                {
                    folder = e.Args[++i];
                    continue;
                }
                if (!e.Args[i].StartsWith("--"))
                    rawPaths.Add(e.Args[i]);
            }

            if (batchMode)
            {
                AttachConsole(-1);

                var files = ResolvePaths(rawPaths, folder);
                if (files.Count == 0)
                {
                    Console.Error.WriteLine("Error: No files to process.");
                    Console.Error.WriteLine("Usage: PolygonOptimizerCore.exe --batch <file1> [file2] ...");
                    Console.Error.WriteLine("       PolygonOptimizerCore.exe --batch --folder <dir>");
                    Console.Error.WriteLine("       PolygonOptimizerCore.exe --batch *.fbx");
                    Shutdown(1);
                    return;
                }

                RunBatch(files);
            }
            else
            {
                string? filePath = rawPaths.FirstOrDefault();
                if (filePath is not null && !File.Exists(filePath))
                {
                    AttachConsole(-1);
                    Console.Error.WriteLine($"Error: File not found: {filePath}");
                    Shutdown(1);
                    return;
                }

                var window = new MainWindow(filePath);
                window.Show();
            }
        }

        private static List<string> ResolvePaths(List<string> rawPaths, string? folder)
        {
            var files = new List<string>();

            // --folder: add all supported files in the directory
            if (folder is not null && Directory.Exists(folder))
            {
                var extensions = new[] { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".ply", ".stl" };
                foreach (var f in Directory.GetFiles(folder))
                {
                    if (extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        files.Add(Path.GetFullPath(f));
                }
            }

            // Expand wildcards and add individual files
            foreach (var raw in rawPaths)
            {
                if (raw.Contains('*') || raw.Contains('?'))
                {
                    var dir = Path.GetDirectoryName(raw);
                    if (string.IsNullOrEmpty(dir)) dir = ".";
                    var pattern = Path.GetFileName(raw);
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.GetFiles(dir, pattern))
                            files.Add(Path.GetFullPath(f));
                    }
                }
                else if (File.Exists(raw))
                {
                    files.Add(Path.GetFullPath(raw));
                }
                else
                {
                    Console.Error.WriteLine($"Warning: File not found, skipping: {raw}");
                }
            }

            return files.Distinct().ToList();
        }

        private void RunBatch(List<string> files)
        {
            Console.WriteLine($"Batch processing {files.Count} file(s)...");
            Console.WriteLine();

            var window = new MainWindow(files[0]);
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Show();

            int currentIndex = 0;

            window.ContentRendered += async (s, ev) =>
            {
                try
                {
                    await ProcessFile(window, files[currentIndex]);
                    currentIndex++;

                    while (currentIndex < files.Count)
                    {
                        var vm = window.DataContext as MainViewModel;
                        vm?.OpenFileFromPath(files[currentIndex]);

                        // Wait for loading to start then finish
                        await System.Threading.Tasks.Task.Delay(200);
                        while (vm?.IsLoading == true)
                            await System.Threading.Tasks.Task.Delay(100);

                        await ProcessFile(window, files[currentIndex]);
                        currentIndex++;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"Done. Processed {files.Count} file(s).");
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Shutdown(1);
                }
            };
        }

        private static async System.Threading.Tasks.Task ProcessFile(MainWindow window, string filePath)
        {
            var vm = window.DataContext as MainViewModel;
            if (vm is null) return;

            // Wait for model to finish loading
            while (vm.IsLoading)
                await System.Threading.Tasks.Task.Delay(100);

            if (vm.TotalTriangleCount == 0)
            {
                Console.Error.WriteLine($"  Skipping {Path.GetFileName(filePath)}: No triangles.");
                return;
            }

            Console.WriteLine($"[{Path.GetFileName(filePath)}]");
            Console.WriteLine($"  Triangles: {vm.TotalTriangleCount}");
            Console.Write("  Iterate Views... ");

            await vm.IterateViewsCommand.ExecuteAsync(null);

            Console.WriteLine($"{vm.SelectedTriangleCount} visible");
            Console.Write("  Invert + Delete... ");

            vm.InvertVisibleSelectionCommand.Execute(null);
            vm.DeleteTrianglesCommand.Execute(null);

            Console.WriteLine($"{vm.TotalTriangleCount} remaining");
            Console.Write("  Export... ");

            vm.ExportToDefaultPath();

            var name = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath).TrimStart('.');
            var dir = Path.GetDirectoryName(filePath) ?? ".";
            var outPath = Path.Combine(dir, name + "_reduced." + ext);
            Console.WriteLine(outPath);
        }
    }
}
