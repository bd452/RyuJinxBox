using Ryujinx.Memory.WindowsShared;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Memory
{
    [SupportedOSPlatform("windows")]
    static class MemoryManagementWindows
    {
        public const int PageSize = 0x1000;

        private static readonly PlaceholderManager _placeholders = new();

        public static nint Allocate(nint size)
        {
            return AllocateInternal(size, AllocationType.Reserve | AllocationType.Commit);
        }

        public static nint Reserve(nint size, bool viewCompatible)
        {
            if (viewCompatible)
            {
                nint baseAddress = AllocateInternal2(size, AllocationType.Reserve | AllocationType.ReservePlaceholder);

                _placeholders.ReserveRange((ulong)baseAddress, (ulong)size);

                return baseAddress;
            }

            return AllocateInternal(size, AllocationType.Reserve);
        }

        private static nint AllocateInternal(nint size, AllocationType flags = 0, MemoryProtection protection = MemoryProtection.ReadWrite)
        {
            nint ptr;

            if (WindowsApi.IsUwpSandbox)
            {
                // Xbox UWP: Use VirtualAllocFromApp which accepts a uint protection parameter
                ptr = WindowsApi.VirtualAllocFromApp(nint.Zero, size, flags, (uint)protection);
            }
            else
            {
                ptr = WindowsApi.VirtualAlloc(nint.Zero, size, flags, protection);
            }

            if (ptr == nint.Zero)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return ptr;
        }

        private static nint AllocateInternal2(nint size, AllocationType flags = 0)
        {
            nint ptr;

            if (WindowsApi.IsUwpSandbox)
            {
                // Xbox UWP: VirtualAlloc2 from KernelBase.dll may not be available.
                // Fall back to VirtualAllocFromApp without placeholder support.
                // This means view-compatible memory won't work, but SoftwarePageTable
                // mode doesn't need it.
                AllocationType fallbackFlags = flags & ~AllocationType.ReservePlaceholder;
                ptr = WindowsApi.VirtualAllocFromApp(nint.Zero, size, fallbackFlags, (uint)MemoryProtection.NoAccess);
            }
            else
            {
                ptr = WindowsApi.VirtualAlloc2(WindowsApi.CurrentProcessHandle, nint.Zero, size, flags, MemoryProtection.NoAccess, nint.Zero, 0);
            }

            if (ptr == nint.Zero)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return ptr;
        }

        public static void Commit(nint location, nint size)
        {
            nint result;

            if (WindowsApi.IsUwpSandbox)
            {
                result = WindowsApi.VirtualAllocFromApp(location, size, AllocationType.Commit, (uint)MemoryProtection.ReadWrite);
            }
            else
            {
                result = WindowsApi.VirtualAlloc(location, size, AllocationType.Commit, MemoryProtection.ReadWrite);
            }

            if (result == nint.Zero)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static void Decommit(nint location, nint size)
        {
            if (!WindowsApi.VirtualFree(location, size, AllocationType.Decommit))
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static void MapView(nint sharedMemory, ulong srcOffset, nint location, nint size, MemoryBlock owner)
        {
            if (WindowsApi.IsUwpSandbox)
            {
                throw new PlatformNotSupportedException(
                    "View-compatible memory mapping (MapViewOfFile3) is not available in the Xbox UWP sandbox. " +
                    "Use MemoryManagerMode.SoftwarePageTable instead of HostMapped/HostMappedUnsafe.");
            }

            _placeholders.MapView(sharedMemory, srcOffset, location, size, owner);
        }

        public static void UnmapView(nint sharedMemory, nint location, nint size, MemoryBlock owner)
        {
            if (WindowsApi.IsUwpSandbox)
            {
                throw new PlatformNotSupportedException(
                    "View-compatible memory mapping (UnmapViewOfFile2) is not available in the Xbox UWP sandbox. " +
                    "Use MemoryManagerMode.SoftwarePageTable instead of HostMapped/HostMappedUnsafe.");
            }

            _placeholders.UnmapView(sharedMemory, location, size, owner);
        }

        public static bool Reprotect(nint address, nint size, MemoryPermission permission, bool forView)
        {
            if (forView)
            {
                return _placeholders.ReprotectView(address, size, permission);
            }

            if (WindowsApi.IsUwpSandbox)
            {
                // Xbox UWP enforces W^X: PAGE_EXECUTE_READWRITE is forbidden.
                // Downgrade RWX requests to RW (callers follow up with RX when done writing).
                if (permission == MemoryPermission.ReadWriteExecute)
                {
                    permission = MemoryPermission.ReadAndWrite;
                }

                return WindowsApi.VirtualProtectFromApp(address, size, (uint)WindowsApi.GetProtection(permission), out _);
            }
            else
            {
                return WindowsApi.VirtualProtect(address, size, WindowsApi.GetProtection(permission), out _);
            }
        }

        public static bool Free(nint address, nint size)
        {
            _placeholders.UnreserveRange((ulong)address, (ulong)size);

            return WindowsApi.VirtualFree(address, nint.Zero, AllocationType.Release);
        }

        public static nint CreateSharedMemory(nint size, bool reserve)
        {
            nint handle;

            if (WindowsApi.IsUwpSandbox)
            {
                // Xbox UWP: CreateFileMappingFromApp uses a different signature
                uint prot = (uint)(FileMapProtection.PageReadWrite |
                    (reserve ? FileMapProtection.SectionReserve : FileMapProtection.SectionCommit));

                handle = WindowsApi.CreateFileMappingFromApp(
                    WindowsApi.InvalidHandleValue,
                    nint.Zero,
                    prot,
                    (ulong)size.ToInt64(),
                    null);
            }
            else
            {
                FileMapProtection prot = reserve ? FileMapProtection.SectionReserve : FileMapProtection.SectionCommit;

                handle = WindowsApi.CreateFileMapping(
                    WindowsApi.InvalidHandleValue,
                    nint.Zero,
                    FileMapProtection.PageReadWrite | prot,
                    (uint)(size.ToInt64() >> 32),
                    (uint)size.ToInt64(),
                    null);
            }

            if (handle == nint.Zero)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return handle;
        }

        public static void DestroySharedMemory(nint handle)
        {
            if (!WindowsApi.CloseHandle(handle))
            {
                throw new ArgumentException("Invalid handle.", nameof(handle));
            }
        }

        public static nint MapSharedMemory(nint handle)
        {
            nint ptr = WindowsApi.MapViewOfFile(handle, 4 | 2, 0, 0, nint.Zero);

            if (ptr == nint.Zero)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return ptr;
        }

        public static void UnmapSharedMemory(nint address)
        {
            if (!WindowsApi.UnmapViewOfFile(address))
            {
                throw new ArgumentException("Invalid address.", nameof(address));
            }
        }
    }
}
