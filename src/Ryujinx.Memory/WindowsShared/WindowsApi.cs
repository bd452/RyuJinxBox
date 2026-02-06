using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Memory.WindowsShared
{
    [SupportedOSPlatform("windows")]
    static partial class WindowsApi
    {
        public static readonly nint InvalidHandleValue = new(-1);
        public static readonly nint CurrentProcessHandle = new(-1);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint VirtualAlloc(
            nint lpAddress,
            nint dwSize,
            AllocationType flAllocationType,
            MemoryProtection flProtect);

        [LibraryImport("KernelBase.dll", SetLastError = true)]
        public static partial nint VirtualAlloc2(
            nint process,
            nint lpAddress,
            nint dwSize,
            AllocationType flAllocationType,
            MemoryProtection flProtect,
            nint extendedParameters,
            ulong parameterCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualProtect(
            nint lpAddress,
            nint dwSize,
            MemoryProtection flNewProtect,
            out MemoryProtection lpflOldProtect);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFree(nint lpAddress, nint dwSize, AllocationType dwFreeType);

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateFileMappingW")]
        public static partial nint CreateFileMapping(
            nint hFile,
            nint lpFileMappingAttributes,
            FileMapProtection flProtect,
            uint dwMaximumSizeHigh,
            uint dwMaximumSizeLow,
            [MarshalAs(UnmanagedType.LPWStr)] string lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint MapViewOfFile(
            nint hFileMappingObject,
            uint dwDesiredAccess,
            uint dwFileOffsetHigh,
            uint dwFileOffsetLow,
            nint dwNumberOfBytesToMap);

        [LibraryImport("KernelBase.dll", SetLastError = true)]
        public static partial nint MapViewOfFile3(
            nint hFileMappingObject,
            nint process,
            nint baseAddress,
            ulong offset,
            nint dwNumberOfBytesToMap,
            ulong allocationType,
            MemoryProtection dwDesiredAccess,
            nint extendedParameters,
            ulong parameterCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnmapViewOfFile(nint lpBaseAddress);

        [LibraryImport("KernelBase.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnmapViewOfFile2(nint process, nint lpBaseAddress, ulong unmapFlags);

        [LibraryImport("kernel32.dll")]
        public static partial uint GetLastError();

        [LibraryImport("kernel32.dll")]
        public static partial int GetCurrentThreadId();

        // --- Xbox UWP sandbox-safe variants ---
        // On Xbox, VirtualAlloc/VirtualProtect are blocked for executable memory.
        // VirtualAllocFromApp and VirtualProtectFromApp are the sandbox-safe equivalents
        // that allow PAGE_EXECUTE_READ (but not PAGE_EXECUTE_READWRITE, enforcing W^X).

        [LibraryImport("api-ms-win-core-memory-l1-1-6.dll", SetLastError = true)]
        public static partial nint VirtualAllocFromApp(
            nint lpAddress,
            nint dwSize,
            AllocationType flAllocationType,
            uint flProtect);

        [LibraryImport("api-ms-win-core-memory-l1-1-6.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualProtectFromApp(
            nint lpAddress,
            nint dwSize,
            uint flNewProtect,
            out uint lpflOldProtect);

        [LibraryImport("api-ms-win-core-memory-l1-1-6.dll", SetLastError = true)]
        public static partial nint CreateFileMappingFromApp(
            nint hFile,
            nint lpFileMappingAttributes,
            uint flProtect,
            ulong dwMaximumSize,
            [MarshalAs(UnmanagedType.LPWStr)] string lpName);

        /// <summary>
        /// Whether the process is running in a UWP sandbox (Xbox Dev Mode or UWP on desktop).
        /// When true, memory operations must use the *FromApp API variants.
        /// </summary>
        public static bool IsUwpSandbox { get; } = DetectUwpSandbox();

        private static bool DetectUwpSandbox()
        {
            // Check if we're running as a UWP/Xbox app by trying to detect the package identity.
            // Apps with package identity are in the UWP sandbox.
            try
            {
                nint lib = System.Runtime.InteropServices.NativeLibrary.Load("api-ms-win-core-memory-l1-1-6.dll");
                if (lib != nint.Zero)
                {
                    bool hasApi = System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, "VirtualAllocFromApp", out _);
                    System.Runtime.InteropServices.NativeLibrary.Free(lib);

                    // Also check for the XBOX define or environment hint
                    string xboxHint = System.Environment.GetEnvironmentVariable("RYUJINX_XBOX");
                    if (xboxHint == "1" || hasApi)
                    {
                        // Verify we actually need FromApp by checking if normal VirtualAlloc
                        // can allocate executable memory. If it fails, we're sandboxed.
                        nint test = VirtualAlloc(nint.Zero, 0x1000,
                            AllocationType.Reserve | AllocationType.Commit,
                            MemoryProtection.ExecuteRead);

                        if (test != nint.Zero)
                        {
                            VirtualFree(test, nint.Zero, AllocationType.Release);
                            return false; // Regular APIs work fine
                        }

                        return true; // Sandboxed - need FromApp variants
                    }
                }
            }
            catch
            {
                // Detection failed, assume not sandboxed
            }

            return false;
        }

        public static MemoryProtection GetProtection(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => MemoryProtection.NoAccess,
                MemoryPermission.Read => MemoryProtection.ReadOnly,
                MemoryPermission.ReadAndWrite => MemoryProtection.ReadWrite,
                MemoryPermission.ReadAndExecute => MemoryProtection.ExecuteRead,
                MemoryPermission.ReadWriteExecute => MemoryProtection.ExecuteReadWrite,
                MemoryPermission.Execute => MemoryProtection.Execute,
                _ => throw new MemoryProtectionException(permission),
            };
        }
    }
}
