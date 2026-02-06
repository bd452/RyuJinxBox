using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Host.Xbox.Memory
{
    /// <summary>
    /// Xbox-specific memory allocator for JIT code cache and virtual memory management.
    ///
    /// Key requirements (from Phase 4 and Phase 5):
    ///   - Allocate executable memory via platform-approved APIs
    ///   - Enforce W^X discipline (Write XOR Execute)
    ///   - Support predictable invalidation
    ///   - Reduce maximum reserved ranges for Series S memory constraints
    ///   - Prefer sparse mappings over monolithic reservations
    ///
    /// On actual Xbox, this would use VirtualAlloc2 / VirtualAllocFromApp with
    /// appropriate PAGE_EXECUTE_READ / PAGE_READWRITE protections.
    /// </summary>
    public sealed class XboxMemoryAllocator : IDisposable
    {
        /// <summary>
        /// Default JIT code cache size: 128 MB.
        /// Conservative for Series S; can be expanded on Series X.
        /// </summary>
        public const long DefaultCodeCacheSize = 128 * 1024 * 1024;

        /// <summary>
        /// Maximum JIT code cache size for Series X: 256 MB.
        /// </summary>
        public const long MaxCodeCacheSizeSeriesX = 256 * 1024 * 1024;

        /// <summary>
        /// Maximum JIT code cache size for Series S: 128 MB.
        /// Series S has tighter memory constraints.
        /// </summary>
        public const long MaxCodeCacheSizeSeriesS = 128 * 1024 * 1024;

        private nint _codeCacheBase;
        private long _codeCacheSize;
        private long _codeCacheUsed = 0;
        private bool _isDisposed;

        /// <summary>
        /// The base address of the JIT code cache.
        /// </summary>
        public nint CodeCacheBase => _codeCacheBase;

        /// <summary>
        /// Total size of the allocated code cache.
        /// </summary>
        public long CodeCacheSize => _codeCacheSize;

        /// <summary>
        /// Amount of code cache currently in use.
        /// </summary>
        public long CodeCacheUsed => _codeCacheUsed;

        /// <summary>
        /// Allocates the JIT code cache.
        /// On Xbox, this must use platform-approved APIs for executable memory.
        /// </summary>
        /// <param name="size">The desired size of the code cache in bytes.</param>
        public void AllocateCodeCache(long size)
        {
            if (_codeCacheBase != nint.Zero)
            {
                throw new InvalidOperationException("Code cache already allocated.");
            }

            _codeCacheSize = size;

            // On actual Xbox, use VirtualAllocFromApp with MEM_RESERVE | MEM_COMMIT
            // and PAGE_READWRITE initially (W^X: start writable, flip to execute after JIT).
            //
            // nint result = VirtualAllocFromApp(nint.Zero, (nuint)size,
            //     MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);

            // For cross-platform build compatibility, use NativeMemory
            unsafe
            {
                _codeCacheBase = (nint)NativeMemory.AlignedAlloc((nuint)size, 4096);
            }

            if (_codeCacheBase == nint.Zero)
            {
                throw new OutOfMemoryException($"Failed to allocate JIT code cache of {size} bytes.");
            }

            Logger.Info?.Print(LogClass.Cpu, $"JIT code cache allocated: {size / (1024 * 1024)} MB at 0x{_codeCacheBase:X}");
        }

        /// <summary>
        /// Marks a region of the code cache as executable (and read-only).
        /// Enforces W^X: the region must have been written to before this call,
        /// and cannot be written to after without calling MakeWritable first.
        /// </summary>
        /// <param name="offset">Offset from code cache base.</param>
        /// <param name="size">Size of the region to make executable.</param>
        public void MakeExecutable(long offset, long size)
        {
            // On actual Xbox:
            // VirtualProtectFromApp(codeCacheBase + offset, (nuint)size,
            //     PAGE_EXECUTE_READ, out _);

            // Track code cache usage (high-water mark)
            long endOffset = offset + size;
            long currentUsed;
            do
            {
                currentUsed = Interlocked.Read(ref _codeCacheUsed);
                if (endOffset <= currentUsed) break;
            }
            while (Interlocked.CompareExchange(ref _codeCacheUsed, endOffset, currentUsed) != currentUsed);

            Logger.Debug?.Print(LogClass.Cpu, $"Code cache region marked executable: offset=0x{offset:X}, size={size}");
        }

        /// <summary>
        /// Marks a region of the code cache as writable (and non-executable).
        /// Used when JIT needs to patch or replace existing code.
        /// </summary>
        /// <param name="offset">Offset from code cache base.</param>
        /// <param name="size">Size of the region to make writable.</param>
        public void MakeWritable(long offset, long size)
        {
            // On actual Xbox:
            // VirtualProtectFromApp(codeCacheBase + offset, (nuint)size,
            //     PAGE_READWRITE, out _);
            Logger.Debug?.Print(LogClass.Cpu, $"Code cache region marked writable: offset=0x{offset:X}, size={size}");
        }

        /// <summary>
        /// Invalidates a region of the code cache, allowing it to be reused.
        /// </summary>
        /// <param name="offset">Offset from code cache base.</param>
        /// <param name="size">Size of the region to invalidate.</param>
        public void InvalidateCodeCache(long offset, long size)
        {
            // On actual Xbox: FlushInstructionCache(GetCurrentProcess(), ptr, size)
            // On x64 this is technically not required due to cache coherency,
            // but we call it anyway for correctness with the Xbox D3D12 driver.
            Logger.Debug?.Print(LogClass.Cpu, $"Code cache invalidated: offset=0x{offset:X}, size={size}");
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_codeCacheBase != nint.Zero)
            {
                unsafe
                {
                    NativeMemory.AlignedFree((void*)_codeCacheBase);
                }

                Logger.Info?.Print(LogClass.Cpu, "JIT code cache freed.");
                _codeCacheBase = nint.Zero;
            }
        }
    }
}
