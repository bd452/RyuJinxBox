using Ryujinx.Common.Logging;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Overrides Vulkan library resolution so that Vulkan calls are routed
    /// through the D3D12-backed Vulkan ICD on Xbox.
    ///
    /// On Xbox there is no native Vulkan driver. Instead, we ship a Vulkan ICD
    /// (like Mesa's dozen driver) that implements Vulkan on top of D3D12.
    /// This class ensures that when Silk.NET.Vulkan or any other library tries
    /// to load vulkan-1.dll, it finds our D3D12-backed implementation.
    /// </summary>
    public static class XboxVulkanLoader
    {
        public static bool IsActive { get; private set; }

        private static nint _icdLibraryHandle;

        /// <summary>
        /// Installs the Vulkan loader override.
        /// This registers a NativeLibrary resolver that intercepts vulkan-1 loads
        /// and redirects them to the D3D12-backed Vulkan ICD.
        /// </summary>
        public static void Install()
        {
            if (IsActive) return;

            // Try to pre-load the ICD library
            _icdLibraryHandle = TryLoadVulkanIcd();

            if (_icdLibraryHandle == nint.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    "Cannot find Vulkan D3D12 ICD library. " +
                    "Using system Vulkan loader (will work on desktop, not on Xbox).");
                return;
            }

            // Register resolver for assemblies that load vulkan-1
            // This covers Silk.NET.Vulkan which is used by the Vulkan renderer
            Assembly[] targetAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in targetAssemblies)
            {
                string name = assembly.GetName().Name ?? "";
                if (name.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Silk", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Ryujinx.Graphics", StringComparison.OrdinalIgnoreCase))
                {
                    NativeLibrary.SetDllImportResolver(assembly, VulkanDllImportResolver);
                }
            }

            IsActive = true;
            Logger.Info?.Print(LogClass.Gpu, "Xbox Vulkan loader override installed.");
        }

        private static nint TryLoadVulkanIcd()
        {
            // Try to load the ICD that DxvkBootstrap found
            nint addr = DxvkBootstrap.GetVkGetInstanceProcAddr();
            if (addr != nint.Zero)
            {
                // The ICD is already loaded by DxvkBootstrap, get its module handle
                string[] names = ["vulkan_dzn.dll", "vulkan_dzn"];
                foreach (string name in names)
                {
                    if (NativeLibrary.TryLoad(name, out nint handle))
                        return handle;
                }
            }

            // Try loading vulkan-1.dll (will work if VK_ICD_FILENAMES is set correctly)
            if (NativeLibrary.TryLoad("vulkan-1.dll", out nint vkHandle))
                return vkHandle;

            return nint.Zero;
        }

        private static nint VulkanDllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (IsVulkanLibrary(libraryName) && _icdLibraryHandle != nint.Zero)
            {
                return _icdLibraryHandle;
            }

            // Let the default resolver handle non-Vulkan libraries
            return nint.Zero;
        }

        private static bool IsVulkanLibrary(string name) =>
            name.Equals("vulkan-1", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("libvulkan", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("libvulkan.so.1", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("libvulkan.dylib", StringComparison.OrdinalIgnoreCase);
    }
}
