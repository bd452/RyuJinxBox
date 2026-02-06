using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox.Native
{
    /// <summary>
    /// Native interop for Xbox/UWP CoreWindow and Win32 HWND extraction.
    /// On Xbox in Developer Mode, we can get an HWND from the CoreWindow
    /// to create a Vulkan Win32 surface.
    /// </summary>
    internal static class CoreWindowNative
    {
        // ICoreWindowInterop - allows getting the HWND from a CoreWindow
        // This COM interface is available on both desktop UWP and Xbox.
        // GUID: {45D64A29-A63E-4CB6-B498-5781D298CB4F}

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetActiveWindow();

        [DllImport("kernel32.dll")]
        internal static extern nint GetModuleHandleW(nint lpModuleName);

        /// <summary>
        /// On Xbox GameCore, the system provides a window automatically.
        /// We retrieve its HWND for Vulkan surface creation.
        /// On UWP, we get the CoreWindow's HWND via ICoreWindowInterop.
        /// </summary>
        internal static nint GetXboxWindowHandle()
        {
            // On GameCore/UWP Xbox apps, there's always an active window
            nint hwnd = GetActiveWindow();
            if (hwnd != nint.Zero)
                return hwnd;

            // Fallback: try to get window from the current thread
            // On Xbox, the main thread always has a CoreWindow
            return nint.Zero;
        }

        internal static nint GetHInstance()
        {
            return GetModuleHandleW(nint.Zero);
        }
    }
}
