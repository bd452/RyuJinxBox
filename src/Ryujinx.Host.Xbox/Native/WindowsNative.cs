using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox.Native
{
    /// <summary>
    /// P/Invoke declarations for Windows/Xbox kernel APIs used by the Xbox host.
    /// On Xbox (UWP/GameCore), some of these are available via *FromApp variants.
    /// </summary>
    internal static class WindowsNative
    {
        // --- Memory Management ---
        // On Xbox UWP, use VirtualAllocFromApp / VirtualProtectFromApp
        // which are the sandbox-safe variants.

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

        // Xbox UWP sandbox-safe variants
        [DllImport("api-ms-win-core-memory-l1-1-6.dll", SetLastError = true)]
        internal static extern nint VirtualAllocFromApp(nint baseAddress, nuint size, uint allocationType, uint protection);

        [DllImport("api-ms-win-core-memory-l1-1-6.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualProtectFromApp(nint address, nuint size, uint newProtection, out uint oldProtection);

        // Memory allocation types
        internal const uint MEM_COMMIT = 0x00001000;
        internal const uint MEM_RESERVE = 0x00002000;
        internal const uint MEM_RELEASE = 0x00008000;
        internal const uint MEM_DECOMMIT = 0x00004000;

        // Memory protection constants
        internal const uint PAGE_NOACCESS = 0x01;
        internal const uint PAGE_READONLY = 0x02;
        internal const uint PAGE_READWRITE = 0x04;
        internal const uint PAGE_EXECUTE = 0x10;
        internal const uint PAGE_EXECUTE_READ = 0x20;
        internal const uint PAGE_EXECUTE_READWRITE = 0x40;

        // --- Exception Handling ---

        [DllImport("kernel32.dll")]
        internal static extern nint AddVectoredExceptionHandler(uint first, nint handler);

        [DllImport("kernel32.dll")]
        internal static extern uint RemoveVectoredExceptionHandler(nint handle);

        // Exception codes
        internal const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;
        internal const uint EXCEPTION_CONTINUE_EXECUTION = 0xFFFFFFFF;
        internal const uint EXCEPTION_CONTINUE_SEARCH = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct EXCEPTION_RECORD
        {
            public uint ExceptionCode;
            public uint ExceptionFlags;
            public nint ExceptionRecord;
            public nint ExceptionAddress;
            public uint NumberParameters;
            // ExceptionInformation[0] = read/write flag (0=read, 1=write, 8=DEP)
            // ExceptionInformation[1] = the virtual address that was accessed
            public nint ExceptionInformation0;
            public nint ExceptionInformation1;
            // ... more entries possible but we only need the first two
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EXCEPTION_POINTERS
        {
            public nint /* EXCEPTION_RECORD* */ ExceptionRecord;
            public nint /* CONTEXT* */ ContextRecord;
        }

        // --- Instruction Cache ---

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();
    }
}
