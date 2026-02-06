using Ryujinx.Common.Logging;
using System;
using System.IO;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Maps emulated Switch filesystem components to Xbox sandbox-safe locations.
    ///
    /// Mapping:
    ///   NAND               → LocalFolder/nand
    ///   SD Card             → LocalFolder/sd
    ///   Shader Cache        → LocalFolder/cache/shaders
    ///   Pipeline Cache      → LocalFolder/cache/pipelines
    ///   Logs                → TemporaryFolder/logs
    ///
    /// No assumptions about symlinks, arbitrary absolute paths, or fast seek-heavy IO.
    /// </summary>
    public sealed class XboxFileSystem
    {
        /// <summary>
        /// Root path for persistent application data (maps to ApplicationData.LocalFolder on Xbox).
        /// </summary>
        public string LocalFolder { get; }

        /// <summary>
        /// Root path for temporary application data (maps to ApplicationData.TemporaryFolder on Xbox).
        /// </summary>
        public string TemporaryFolder { get; }

        /// <summary>
        /// Path to the emulated NAND storage.
        /// </summary>
        public string NandPath => Path.Combine(LocalFolder, "nand");

        /// <summary>
        /// Path to the emulated SD card storage.
        /// </summary>
        public string SdCardPath => Path.Combine(LocalFolder, "sd");

        /// <summary>
        /// Path to the shader cache directory.
        /// </summary>
        public string ShaderCachePath => Path.Combine(LocalFolder, "cache", "shaders");

        /// <summary>
        /// Path to the pipeline cache directory.
        /// </summary>
        public string PipelineCachePath => Path.Combine(LocalFolder, "cache", "pipelines");

        /// <summary>
        /// Path to the logs directory.
        /// </summary>
        public string LogsPath => Path.Combine(TemporaryFolder, "logs");

        /// <summary>
        /// Path to game save data.
        /// </summary>
        public string SaveDataPath => Path.Combine(LocalFolder, "save");

        /// <summary>
        /// Path to system firmware data.
        /// </summary>
        public string SystemPath => Path.Combine(LocalFolder, "system");

        /// <summary>
        /// Creates a new XboxFileSystem with the specified root folders.
        /// On an actual Xbox, these would come from Windows.Storage.ApplicationData.Current.
        /// </summary>
        /// <param name="localFolder">The persistent local storage root.</param>
        /// <param name="temporaryFolder">The temporary storage root.</param>
        public XboxFileSystem(string localFolder, string temporaryFolder)
        {
            LocalFolder = localFolder ?? throw new ArgumentNullException(nameof(localFolder));
            TemporaryFolder = temporaryFolder ?? throw new ArgumentNullException(nameof(temporaryFolder));
        }

        /// <summary>
        /// Ensures all required directories exist.
        /// Should be called during application initialization.
        /// </summary>
        public void EnsureDirectoriesExist()
        {
            string[] directories =
            [
                NandPath,
                SdCardPath,
                ShaderCachePath,
                PipelineCachePath,
                LogsPath,
                SaveDataPath,
                SystemPath,
            ];

            foreach (string dir in directories)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    Logger.Info?.Print(LogClass.Application, $"Ensured directory exists: {dir}");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Failed to create directory '{dir}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Maps a relative path intended for the emulated NAND to a full Xbox-safe path.
        /// </summary>
        /// <param name="relativePath">The relative path within NAND.</param>
        /// <returns>The full sandbox-safe path.</returns>
        public string ResolveNandPath(string relativePath)
        {
            return ResolveSafe(NandPath, relativePath);
        }

        /// <summary>
        /// Maps a relative path intended for the emulated SD card to a full Xbox-safe path.
        /// </summary>
        /// <param name="relativePath">The relative path within SD.</param>
        /// <returns>The full sandbox-safe path.</returns>
        public string ResolveSdCardPath(string relativePath)
        {
            return ResolveSafe(SdCardPath, relativePath);
        }

        /// <summary>
        /// Safely resolves a path within a root directory, preventing traversal outside the root.
        /// </summary>
        private static string ResolveSafe(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return root;
            }

            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

            // Ensure the resolved path is still within the root
            if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Attempted path traversal outside sandbox root. Root: '{root}', Requested: '{relativePath}'");
            }

            return fullPath;
        }
    }
}
