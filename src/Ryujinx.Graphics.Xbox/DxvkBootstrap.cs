using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Bootstraps the Vulkan-over-D3D12 ICD for Xbox.
    ///
    /// Architecture:
    ///   Ryujinx Vulkan Renderer (SPIR-V)
    ///         ↓
    ///   Vulkan ICD (vulkan_d3d12.dll - Mesa Dozen or equivalent)
    ///         ↓
    ///   Xbox D3D12 / GameCore
    ///
    /// This is NOT DXVK (which goes DirectX → Vulkan, the wrong direction).
    /// Instead, we use a Vulkan ICD that implements Vulkan on top of D3D12,
    /// such as Mesa's "dozen" driver or a purpose-built translation layer.
    ///
    /// The Vulkan ICD is loaded by overriding VK_ICD_FILENAMES to point to
    /// our shipped ICD manifest, bypassing the default Vulkan loader discovery.
    /// </summary>
    public static class DxvkBootstrap
    {
        /// <summary>
        /// Name of the Vulkan-over-D3D12 ICD library.
        /// Mesa's dozen driver or equivalent implementation.
        /// </summary>
        private const string VulkanIcdLibraryName = "vulkan_dzn";
        private const string VulkanIcdManifestName = "xbox_vulkan_icd.json";

        public static bool IsInitialized { get; private set; }
        private static nint _libraryHandle;

        /// <summary>
        /// Initializes the Vulkan-over-D3D12 ICD for Xbox.
        ///
        /// Steps:
        /// 1. Disable implicit Vulkan layers (no desktop GPU drivers on Xbox)
        /// 2. Point VK_ICD_FILENAMES to our D3D12-backed Vulkan ICD
        /// 3. Load the ICD library to verify it's present
        /// </summary>
        /// <param name="icdPath">Optional explicit path to the ICD library.</param>
        public static void Initialize(string icdPath = null)
        {
            if (IsInitialized)
            {
                Logger.Warning?.Print(LogClass.Gpu, "Vulkan D3D12 ICD already initialized.");
                return;
            }

            Logger.Info?.Print(LogClass.Gpu, "Initializing Vulkan-over-D3D12 ICD for Xbox...");

            // Disable implicit desktop Vulkan layers
            DisableImplicitLayers();

            // Find and register the ICD
            string manifestPath = icdPath ?? FindIcdManifest();

            if (!string.IsNullOrEmpty(manifestPath))
            {
                // Tell the Vulkan loader to use only our ICD
                Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", manifestPath);
                Environment.SetEnvironmentVariable("VK_DRIVER_FILES", manifestPath);
                Logger.Info?.Print(LogClass.Gpu, $"VK_ICD_FILENAMES set to: {manifestPath}");
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    "Vulkan D3D12 ICD manifest not found. Falling back to system Vulkan loader. " +
                    "This is expected on non-Xbox platforms.");
            }

            // Try to load the ICD library directly to verify it exists
            string libraryPath = FindIcdLibrary();
            if (!string.IsNullOrEmpty(libraryPath) && NativeLibrary.TryLoad(libraryPath, out _libraryHandle))
            {
                IsInitialized = true;
                Logger.Info?.Print(LogClass.Gpu, $"Vulkan D3D12 ICD loaded: {libraryPath}");
            }
            else
            {
                // Not fatal - the Vulkan loader may still find a usable ICD
                Logger.Warning?.Print(LogClass.Gpu,
                    "Could not directly load Vulkan D3D12 ICD. " +
                    "The Vulkan loader will attempt standard driver discovery.");
            }
        }

        private static void DisableImplicitLayers()
        {
            Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "*");
            Environment.SetEnvironmentVariable("VK_LAYER_PATH", "");
            Logger.Info?.Print(LogClass.Gpu, "Implicit Vulkan layers disabled.");
        }

        private static string FindIcdManifest()
        {
            string[] searchPaths =
            [
                Path.Combine(AppContext.BaseDirectory, VulkanIcdManifestName),
                Path.Combine(AppContext.BaseDirectory, "vulkan", VulkanIcdManifestName),
                Path.Combine(AppContext.BaseDirectory, "external", "vulkan", VulkanIcdManifestName),
            ];

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }

            return null;
        }

        private static string FindIcdLibrary()
        {
            string dllName = $"{VulkanIcdLibraryName}.dll";
            string[] searchPaths =
            [
                Path.Combine(AppContext.BaseDirectory, dllName),
                Path.Combine(AppContext.BaseDirectory, "vulkan", dllName),
                Path.Combine(AppContext.BaseDirectory, "external", "vulkan", dllName),
            ];

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Gets vkGetInstanceProcAddr from the loaded ICD.
        /// Used to override Vulkan function resolution.
        /// </summary>
        public static nint GetVkGetInstanceProcAddr()
        {
            if (_libraryHandle == nint.Zero) return nint.Zero;

            if (NativeLibrary.TryGetExport(_libraryHandle, "vkGetInstanceProcAddr", out nint addr))
                return addr;

            return nint.Zero;
        }

        public static void Shutdown()
        {
            if (_libraryHandle != nint.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = nint.Zero;
            }

            IsInitialized = false;
            Logger.Info?.Print(LogClass.Gpu, "Vulkan D3D12 ICD shut down.");
        }
    }
}
