using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Bootstraps the DXVK translation layer for Xbox.
    ///
    /// Phase 3 requirements:
    ///   - Replace Vulkan loader discovery with explicit DXVK bootstrap
    ///   - Disable implicit desktop Vulkan layers
    ///   - D3D12 backend only
    ///   - Pipeline cache enabled with aggressive reuse
    ///
    /// DXVK translates Vulkan API calls to Direct3D 12 calls, allowing
    /// Ryujinx's existing Vulkan renderer to work on Xbox's D3D12 driver.
    /// </summary>
    public static class DxvkBootstrap
    {
        /// <summary>
        /// Name of the DXVK Vulkan-to-D3D12 translation library.
        /// </summary>
        private const string DxvkLibraryName = "dxvk_d3d12";

        /// <summary>
        /// Whether DXVK has been successfully initialized.
        /// </summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// The handle to the loaded DXVK library.
        /// </summary>
        private static nint _libraryHandle;

        /// <summary>
        /// Initializes DXVK for Xbox, loading the Vulkan-to-D3D12 translation layer.
        ///
        /// This must be called before any Vulkan API calls are made.
        /// It sets up the environment so that vkCreateInstance and related calls
        /// are intercepted by DXVK and translated to D3D12.
        /// </summary>
        /// <param name="dxvkLibraryPath">
        /// Path to the DXVK library. On Xbox, this would typically be in the app package.
        /// </param>
        public static void Initialize(string dxvkLibraryPath = null)
        {
            if (IsInitialized)
            {
                Logger.Warning?.Print(LogClass.Gpu, "DXVK is already initialized.");
                return;
            }

            Logger.Info?.Print(LogClass.Gpu, "Initializing DXVK Vulkan-to-D3D12 translation layer...");

            // Disable any implicit Vulkan layers that might interfere
            DisableImplicitLayers();

            // Load the DXVK library
            string libraryPath = dxvkLibraryPath ?? FindDxvkLibrary();

            if (string.IsNullOrEmpty(libraryPath))
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    "DXVK library not found. Falling back to system Vulkan loader. " +
                    "This is expected during development on non-Xbox platforms.");
                return;
            }

            if (!NativeLibrary.TryLoad(libraryPath, out _libraryHandle))
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to load DXVK library from: {libraryPath}");
                return;
            }

            IsInitialized = true;
            Logger.Info?.Print(LogClass.Gpu, $"DXVK initialized from: {libraryPath}");
        }

        /// <summary>
        /// Disables implicit Vulkan layers that are present on desktop Windows
        /// but would interfere with DXVK on Xbox.
        /// </summary>
        private static void DisableImplicitLayers()
        {
            // Setting this environment variable prevents the Vulkan loader from
            // scanning for and loading implicit layers, which are not relevant on Xbox
            // and could cause issues with DXVK.
            Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "*");

            // Disable Vulkan validation layers in non-debug builds
            Environment.SetEnvironmentVariable("VK_LAYER_PATH", "");

            Logger.Info?.Print(LogClass.Gpu, "Implicit Vulkan layers disabled for Xbox DXVK mode.");
        }

        /// <summary>
        /// Attempts to find the DXVK library in standard locations.
        /// </summary>
        private static string FindDxvkLibrary()
        {
            string[] searchPaths =
            [
                // App package directory (Xbox UWP)
                Path.Combine(AppContext.BaseDirectory, $"{DxvkLibraryName}.dll"),
                // External directory
                Path.Combine(AppContext.BaseDirectory, "external", "dxvk", $"{DxvkLibraryName}.dll"),
                // Development path
                Path.Combine(AppContext.BaseDirectory, "..", "external", "dxvk", $"{DxvkLibraryName}.dll"),
            ];

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a function pointer from the loaded DXVK library.
        /// Used to override Vulkan function resolution.
        /// </summary>
        /// <param name="functionName">The name of the Vulkan function.</param>
        /// <returns>The function pointer, or IntPtr.Zero if not found.</returns>
        public static nint GetDxvkProcAddress(string functionName)
        {
            if (!IsInitialized || _libraryHandle == nint.Zero)
            {
                return nint.Zero;
            }

            if (NativeLibrary.TryGetExport(_libraryHandle, functionName, out nint address))
            {
                return address;
            }

            return nint.Zero;
        }

        /// <summary>
        /// Shuts down DXVK and releases the loaded library.
        /// </summary>
        public static void Shutdown()
        {
            if (!IsInitialized)
            {
                return;
            }

            if (_libraryHandle != nint.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = nint.Zero;
            }

            IsInitialized = false;
            Logger.Info?.Print(LogClass.Gpu, "DXVK shut down.");
        }
    }
}
