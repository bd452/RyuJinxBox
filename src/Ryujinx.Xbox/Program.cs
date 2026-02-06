using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ryujinx.Xbox
{
    internal static class Program
    {
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

            // Check for keys - also try USB drives
            string keysPath = Path.Combine(LocalFolder, "system", "prod.keys");
            if (!File.Exists(keysPath))
            {
                // Try to find keys on USB
                string usbKeys = FindFileOnUsb("prod.keys");
                if (usbKeys != null)
                {
                    Logger.Notice.Print(LogClass.Application, $"Found prod.keys on USB: {usbKeys}");
                    Directory.CreateDirectory(Path.GetDirectoryName(keysPath)!);
                    File.Copy(usbKeys, keysPath, true);
                    Logger.Notice.Print(LogClass.Application, "Copied prod.keys to local storage.");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Application,
                        "prod.keys not found. Commercial games will not load.");
                    Logger.Warning?.Print(LogClass.Application,
                        "Place prod.keys on a USB drive or upload via Device Portal to LocalState/system/");
                }
            }

            // Find a ROM: command line arg > LocalFolder/roms/ > USB drives
            string romPath = args.Length > 0 ? args[0] : null;

            if (string.IsNullOrEmpty(romPath) || !File.Exists(romPath))
            {
                romPath = FindRom();
            }

            if (string.IsNullOrEmpty(romPath))
            {
                Logger.Error?.Print(LogClass.Application, "No ROM found. Place ROM files in one of:");
                Logger.Error?.Print(LogClass.Application, $"  - USB drive (any folder)");
                Logger.Error?.Print(LogClass.Application, $"  - {Path.Combine(LocalFolder, "roms")}");
                Logger.Error?.Print(LogClass.Application, "Supported: .xci, .nsp, .nro, .nca, .nso, .pfs0");
                Logger.Error?.Print(LogClass.Application, "");
                Logger.Error?.Print(LogClass.Application, "USB: Plug a USB drive with ROM files into your Xbox.");
                Logger.Error?.Print(LogClass.Application, "Device Portal: Upload to LocalState/roms/");

                System.Threading.Thread.Sleep(15000);
                return;
            }

            Logger.Notice.Print(LogClass.Application, $"Loading: {romPath}");
            XboxProgram.Run(LocalFolder, TemporaryFolder, romPath);
        }

        private static void ResolvePaths()
        {
            // Try UWP ApplicationData paths (Xbox and UWP desktop)
            try
            {
                Type appDataType = Type.GetType(
                    "Windows.Storage.ApplicationData, Windows.Storage, ContentType=WindowsRuntime");
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
                // Not UWP
            }

            // Desktop fallback
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ryujinx.Xbox");

            LocalFolder = Path.Combine(baseDir, "LocalState");
            TemporaryFolder = Path.Combine(baseDir, "TempState");
        }

        private static void EnsureDirectories()
        {
            foreach (string dir in new[]
            {
                LocalFolder, TemporaryFolder,
                Path.Combine(LocalFolder, "roms"),
                Path.Combine(LocalFolder, "system"),
                Path.Combine(LocalFolder, "nand"),
                Path.Combine(LocalFolder, "sd"),
            })
            {
                Directory.CreateDirectory(dir);
            }
        }

        /// <summary>
        /// Finds a ROM to load. Search order:
        /// 1. LocalFolder/roms/ (uploaded via Device Portal)
        /// 2. USB removable drives (any folder, recursive)
        /// </summary>
        private static string FindRom()
        {
            List<string> allRoms = [];

            // 1. Scan LocalFolder/roms/
            string romsDir = Path.Combine(LocalFolder, "roms");
            if (Directory.Exists(romsDir))
            {
                allRoms.AddRange(ScanDirectoryForRoms(romsDir, recursive: true));
            }

            // 2. Scan USB / removable drives
            var usbRoms = ScanUsbDrivesForRoms();
            allRoms.AddRange(usbRoms);

            if (allRoms.Count == 0)
                return null;

            // Sort alphabetically
            allRoms.Sort(StringComparer.OrdinalIgnoreCase);

            Logger.Notice.Print(LogClass.Application, $"Found {allRoms.Count} ROM(s):");
            foreach (string rom in allRoms)
            {
                Logger.Notice.Print(LogClass.Application, $"  - {rom}");
            }

            // Load the first one
            Logger.Notice.Print(LogClass.Application, $"Selected: {Path.GetFileName(allRoms[0])}");
            return allRoms[0];
        }

        /// <summary>
        /// Scans all removable/USB drives for ROM files.
        /// On Xbox with removableStorage capability and file type associations,
        /// the app can read files with declared extensions from USB drives.
        /// </summary>
        private static List<string> ScanUsbDrivesForRoms()
        {
            List<string> roms = [];

            try
            {
                // Method 1: Enumerate DriveInfo for removable drives
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType == DriveType.Removable && drive.IsReady)
                        {
                            Logger.Info?.Print(LogClass.Application,
                                $"Scanning USB drive: {drive.Name} ({drive.VolumeLabel})");

                            roms.AddRange(ScanDirectoryForRoms(drive.RootDirectory.FullName, recursive: true));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Application,
                            $"Could not scan drive {drive.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Drive enumeration failed: {ex.Message}");
            }

            // Method 2: Try known Xbox USB mount points
            // On Xbox, USB drives can appear at D:\ or other drive letters
            string[] xboxUsbPaths = ["D:\\", "E:\\", "F:\\", "G:\\"];
            foreach (string usbPath in xboxUsbPaths)
            {
                try
                {
                    if (Directory.Exists(usbPath))
                    {
                        var found = ScanDirectoryForRoms(usbPath, recursive: true);
                        // Avoid duplicates from Method 1
                        foreach (string rom in found)
                        {
                            if (!roms.Contains(rom, StringComparer.OrdinalIgnoreCase))
                                roms.Add(rom);
                        }
                    }
                }
                catch
                {
                    // Drive not accessible
                }
            }

            // Method 3: Try WinRT KnownFolders.RemovableDevices
            try
            {
                Type knownFoldersType = Type.GetType(
                    "Windows.Storage.KnownFolders, Windows.Storage, ContentType=WindowsRuntime");
                if (knownFoldersType != null)
                {
                    dynamic removableDevices = knownFoldersType
                        .GetProperty("RemovableDevices")?.GetValue(null);

                    if (removableDevices != null)
                    {
                        // GetFoldersAsync returns IReadOnlyList<StorageFolder>
                        dynamic folders = removableDevices.GetFoldersAsync().GetAwaiter().GetResult();
                        foreach (dynamic folder in folders)
                        {
                            string folderPath = folder.Path;
                            Logger.Info?.Print(LogClass.Application,
                                $"Found removable device via WinRT: {folderPath}");

                            roms.AddRange(ScanDirectoryForRoms(folderPath, recursive: true));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug?.Print(LogClass.Application,
                    $"WinRT removable device enumeration not available: {ex.Message}");
            }

            return roms;
        }

        /// <summary>
        /// Scans a directory for ROM files with supported extensions.
        /// </summary>
        private static List<string> ScanDirectoryForRoms(string directory, bool recursive)
        {
            List<string> roms = [];

            try
            {
                SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                foreach (string file in Directory.EnumerateFiles(directory, "*.*", option))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (RomExtensions.Contains(ext))
                    {
                        roms.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Some directories may not be accessible, skip them
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"Error scanning {directory}: {ex.Message}");
            }

            return roms;
        }

        /// <summary>
        /// Finds a specific file on USB drives (used for prod.keys).
        /// </summary>
        private static string FindFileOnUsb(string fileName)
        {
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType == DriveType.Removable && drive.IsReady)
                        {
                            foreach (string file in Directory.EnumerateFiles(
                                drive.RootDirectory.FullName, fileName, SearchOption.AllDirectories))
                            {
                                return file;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Also try known Xbox drive letters
            foreach (string path in new[] { "D:\\", "E:\\", "F:\\" })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        foreach (string file in Directory.EnumerateFiles(path, fileName, SearchOption.AllDirectories))
                        {
                            return file;
                        }
                    }
                }
                catch { }
            }

            return null;
        }
    }

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
