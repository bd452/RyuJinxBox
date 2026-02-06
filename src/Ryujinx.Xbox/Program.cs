using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Xbox
{
    internal static class Program
    {
        // On Xbox UWP/GameCore, these paths come from Windows.Storage.ApplicationData.Current
        // On desktop Windows for testing, we use a local directory
        private static string LocalFolder;
        private static string TemporaryFolder;

        private static readonly string[] RomExtensions = [".xci", ".nsp", ".nca", ".nro", ".nso", ".pfs0"];

        [STAThread]
        static void Main(string[] args)
        {
            ResolvePaths();

            Logger.SetEnable(LogLevel.Info, true);
            Logger.SetEnable(LogLevel.Warning, true);
            Logger.SetEnable(LogLevel.Error, true);
            Logger.SetEnable(LogLevel.Notice, true);
            Logger.SetEnable(LogLevel.Stub, true);
            Logger.AddTarget(new ConsoleLogTarget("console"));

            Logger.Notice.Print(LogClass.Application, "=== Ryujinx Xbox ===");
            Logger.Notice.Print(LogClass.Application, $"LocalFolder:     {LocalFolder}");
            Logger.Notice.Print(LogClass.Application, $"TemporaryFolder: {TemporaryFolder}");

            EnsureDirectories();

            // If a ROM path was passed as argument, use it directly
            string romPath = args.Length > 0 ? args[0] : null;

            // Otherwise scan for ROMs
            if (string.IsNullOrEmpty(romPath) || !File.Exists(romPath))
            {
                romPath = FindRom();
            }

            if (string.IsNullOrEmpty(romPath))
            {
                Logger.Error?.Print(LogClass.Application,
                    "No ROM found. Place ROM files (.xci, .nsp, .nro) in:");
                Logger.Error?.Print(LogClass.Application, $"  {Path.Combine(LocalFolder, "roms")}");
                Logger.Error?.Print(LogClass.Application, "Or pass a ROM path as a command line argument.");
                Logger.Error?.Print(LogClass.Application, "On Xbox, use Device Portal to upload files to LocalState/roms/");

                // Wait so the user can read the message in the Xbox Dev Mode console
                System.Threading.Thread.Sleep(10000);
                return;
            }

            Logger.Notice.Print(LogClass.Application, $"Loading: {romPath}");

            // Check for keys
            string keysPath = Path.Combine(LocalFolder, "system", "prod.keys");
            if (!File.Exists(keysPath))
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"prod.keys not found at {keysPath}. Commercial games will not load.");
                Logger.Warning?.Print(LogClass.Application,
                    "Upload prod.keys via Device Portal to LocalState/system/prod.keys");
            }

            XboxProgram.Run(LocalFolder, TemporaryFolder, romPath);
        }

        private static void ResolvePaths()
        {
            // Try to get UWP ApplicationData paths (works on Xbox and UWP desktop)
            try
            {
                // Windows.Storage.ApplicationData.Current.LocalFolder.Path
                // We use reflection to avoid a hard dependency on WinRT
                Type appDataType = Type.GetType("Windows.Storage.ApplicationData, Windows.Storage, ContentType=WindowsRuntime");
                if (appDataType != null)
                {
                    dynamic appData = appDataType.GetProperty("Current")?.GetValue(null);
                    if (appData != null)
                    {
                        LocalFolder = appData.LocalFolder.Path;
                        TemporaryFolder = appData.TemporaryFolder.Path;
                        return;
                    }
                }
            }
            catch
            {
                // Not running as UWP, fall through to desktop paths
            }

            // Desktop fallback for testing
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ryujinx.Xbox");

            LocalFolder = Path.Combine(baseDir, "LocalState");
            TemporaryFolder = Path.Combine(baseDir, "TempState");
        }

        private static void EnsureDirectories()
        {
            string[] dirs =
            [
                LocalFolder,
                TemporaryFolder,
                Path.Combine(LocalFolder, "roms"),
                Path.Combine(LocalFolder, "system"),
                Path.Combine(LocalFolder, "nand"),
                Path.Combine(LocalFolder, "sd"),
            ];

            foreach (string dir in dirs)
            {
                Directory.CreateDirectory(dir);
            }
        }

        /// <summary>
        /// Scans LocalFolder/roms/ for ROM files.
        /// If multiple are found, picks the first one alphabetically.
        /// On Xbox, users upload ROMs via Device Portal to LocalState/roms/
        /// </summary>
        private static string FindRom()
        {
            string romsDir = Path.Combine(LocalFolder, "roms");

            if (!Directory.Exists(romsDir))
                return null;

            var romFiles = Directory.GetFiles(romsDir)
                .Where(f => RomExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            if (romFiles.Count == 0)
            {
                // Also check subdirectories one level deep
                foreach (string subDir in Directory.GetDirectories(romsDir))
                {
                    romFiles.AddRange(Directory.GetFiles(subDir)
                        .Where(f => RomExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
                }
            }

            if (romFiles.Count == 0)
                return null;

            if (romFiles.Count > 1)
            {
                Logger.Notice.Print(LogClass.Application, $"Found {romFiles.Count} ROMs:");
                foreach (string rom in romFiles)
                {
                    Logger.Notice.Print(LogClass.Application, $"  - {Path.GetFileName(rom)}");
                }
                Logger.Notice.Print(LogClass.Application, $"Loading first: {Path.GetFileName(romFiles[0])}");
            }

            return romFiles[0];
        }
    }

    /// <summary>
    /// Simple console log target for Xbox Dev Mode console output.
    /// </summary>
    internal class ConsoleLogTarget : Ryujinx.Common.Logging.Targets.ILogTarget
    {
        public string Name { get; }

        public ConsoleLogTarget(string name) => Name = name;

        public void Log(object sender, LogEventArgs args)
        {
            Console.WriteLine($"[{args.Level}] {args.Message}");
        }

        public void Dispose() { }
    }
}
