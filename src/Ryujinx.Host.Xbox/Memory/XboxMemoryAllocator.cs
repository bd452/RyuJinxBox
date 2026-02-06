using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Native;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Host.Xbox.Memory
{
    /// <summary>
    /// Xbox-specific memory allocator for JIT code cache.
    /// Uses VirtualAllocFromApp / VirtualProtectFromApp for Xbox UWP sandbox compatibility.
    /// Falls back to VirtualAlloc / VirtualProtect on desktop Windows.
    ///
    /// Enforces W^X discipline: memory is either writable or executable, never both.
    /// </summary>
    public sealed class XboxMemoryAllocator : IDisposable
    {
        public const long DefaultCodeCacheSize = 128 * 1024 * 1024;
        public const long MaxCodeCacheSizeSeriesX = 256 * 1024 * 1024;
        public const long MaxCodeCacheSizeSeriesS = 128 * 1024 * 1024;

        private nint _codeCacheBase;
        private long _codeCacheSize;
        private long _codeCacheUsed;
        private bool _isDisposed;
        private readonly bool _useFromAppApis;

        public nint CodeCacheBase => _codeCacheBase;
        public long CodeCacheSize => _codeCacheSize;
        public long CodeCacheUsed => Interlocked.Read(ref _codeCacheUsed);

        public XboxMemoryAllocator()
        {
            // Try to detect if we're in a UWP sandbox (Xbox) and should use *FromApp APIs.
            // On Xbox, the non-FromApp variants will fail.
            _useFromAppApis = ShouldUseFromAppApis();
        }

        private static bool ShouldUseFromAppApis()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            // On Xbox, VirtualAllocFromApp is the only allowed path.
            // Detect by trying to load the API; if available, prefer it.
            try
            {
                nint lib = NativeLibrary.Load("api-ms-win-core-memory-l1-1-6.dll");
                if (lib != nint.Zero)
                {
                    bool hasApi = NativeLibrary.TryGetExport(lib, "VirtualAllocFromApp", out _);
                    NativeLibrary.Free(lib);
                    return hasApi;
                }
            }
            catch
            {
                // Not available, use standard APIs
            }
            return false;
        }

        /// <summary>
        /// Allocates the JIT code cache with PAGE_READWRITE protection (W^X: start writable).
        /// </summary>
        public void AllocateCodeCache(long size)
        {
            if (_codeCacheBase != nint.Zero)
                throw new InvalidOperationException("Code cache already allocated.");

            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("XboxMemoryAllocator requires Windows.");

            _codeCacheSize = size;
            uint allocType = WindowsNative.MEM_RESERVE | WindowsNative.MEM_COMMIT;
            uint protection = WindowsNative.PAGE_READWRITE;

            nint result;
            if (_useFromAppApis)
            {
                result = WindowsNative.VirtualAllocFromApp(nint.Zero, (nuint)size, allocType, protection);
            }
            else
            {
                result = WindowsNative.VirtualAlloc(nint.Zero, (nuint)size, allocType, protection);
            }

            if (result == nint.Zero)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new OutOfMemoryException(
                    $"Failed to allocate JIT code cache of {size / (1024 * 1024)} MB. Win32 error: 0x{error:X8}");
            }

            _codeCacheBase = result;
            Logger.Info?.Print(LogClass.Cpu,
                $"JIT code cache allocated: {size / (1024 * 1024)} MB at 0x{result:X16} (FromApp={_useFromAppApis})");
        }

        /// <summary>
        /// Marks a region as executable and read-only (W^X: flip to execute).
        /// </summary>
        public void MakeExecutable(long offset, long size)
        {
            if (_codeCacheBase == nint.Zero) return;

            nint address = _codeCacheBase + (nint)offset;
            uint newProtection = WindowsNative.PAGE_EXECUTE_READ;

            bool success;
            if (_useFromAppApis)
            {
                success = WindowsNative.VirtualProtectFromApp(address, (nuint)size, newProtection, out _);
            }
            else
            {
                success = WindowsNative.VirtualProtect(address, (nuint)size, newProtection, out _);
            }

            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                Logger.Error?.Print(LogClass.Cpu,
                    $"MakeExecutable failed at offset 0x{offset:X}, size {size}. Error: 0x{error:X8}");
                return;
            }

            // Flush instruction cache for the modified region
            WindowsNative.FlushInstructionCache(WindowsNative.GetCurrentProcess(), address, (nuint)size);

            // Track high-water mark
            long endOffset = offset + size;
            long current;
            do
            {
                current = Interlocked.Read(ref _codeCacheUsed);
                if (endOffset <= current) break;
            } while (Interlocked.CompareExchange(ref _codeCacheUsed, endOffset, current) != current);
        }

        /// <summary>
        /// Marks a region as writable and non-executable (W^X: flip to write).
        /// </summary>
        public void MakeWritable(long offset, long size)
        {
            if (_codeCacheBase == nint.Zero) return;

            nint address = _codeCacheBase + (nint)offset;
            uint newProtection = WindowsNative.PAGE_READWRITE;

            bool success;
            if (_useFromAppApis)
            {
                success = WindowsNative.VirtualProtectFromApp(address, (nuint)size, newProtection, out _);
            }
            else
            {
                success = WindowsNative.VirtualProtect(address, (nuint)size, newProtection, out _);
            }

            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                Logger.Error?.Print(LogClass.Cpu,
                    $"MakeWritable failed at offset 0x{offset:X}, size {size}. Error: 0x{error:X8}");
            }
        }

        /// <summary>
        /// Flushes the instruction cache for a code region.
        /// Required after JIT code generation on some architectures.
        /// </summary>
        public void InvalidateCodeCache(long offset, long size)
        {
            if (_codeCacheBase == nint.Zero) return;

            nint address = _codeCacheBase + (nint)offset;
            WindowsNative.FlushInstructionCache(WindowsNative.GetCurrentProcess(), address, (nuint)size);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_codeCacheBase != nint.Zero)
            {
                WindowsNative.VirtualFree(_codeCacheBase, 0, WindowsNative.MEM_RELEASE);
                Logger.Info?.Print(LogClass.Cpu, "JIT code cache freed.");
                _codeCacheBase = nint.Zero;
            }
        }
    }
}
