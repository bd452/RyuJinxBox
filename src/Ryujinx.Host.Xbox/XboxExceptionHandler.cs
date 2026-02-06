using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Xbox/Windows exception handler using Vectored Exception Handling (VEH).
    /// Intercepts access violations from JIT-compiled code and routes them
    /// through the emulator's fault handling system.
    ///
    /// Replaces POSIX signal-based fault handling used on Linux/macOS.
    /// </summary>
    public static unsafe class XboxExceptionHandler
    {
        public delegate bool FaultHandler(ulong faultAddress);

        private static FaultHandler _faultHandler;
        private static nint _vehHandle;
        private static readonly delegate* unmanaged[Stdcall]<WindowsNative.EXCEPTION_POINTERS*, int> _vectoredHandlerPtr;

        static XboxExceptionHandler()
        {
            // Pre-create the unmanaged function pointer for the VEH callback
            _vectoredHandlerPtr = &VectoredExceptionHandler;
        }

        /// <summary>
        /// Installs the vectored exception handler.
        /// </summary>
        public static void Install(FaultHandler handler)
        {
            if (!OperatingSystem.IsWindows())
            {
                Logger.Warning?.Print(LogClass.Cpu, "XboxExceptionHandler is Windows-only. Skipping install.");
                return;
            }

            _faultHandler = handler ?? throw new ArgumentNullException(nameof(handler));

            // Register as first-chance handler (first=1) so we see exceptions before any frame-based handlers
            _vehHandle = WindowsNative.AddVectoredExceptionHandler(1, (nint)_vectoredHandlerPtr);

            if (_vehHandle == nint.Zero)
            {
                Logger.Error?.Print(LogClass.Cpu, "Failed to install vectored exception handler.");
                return;
            }

            Logger.Info?.Print(LogClass.Cpu, "Vectored exception handler installed for JIT fault dispatch.");
        }

        /// <summary>
        /// Removes the vectored exception handler.
        /// </summary>
        public static void Uninstall()
        {
            if (_vehHandle != nint.Zero)
            {
                WindowsNative.RemoveVectoredExceptionHandler(_vehHandle);
                _vehHandle = nint.Zero;
                Logger.Info?.Print(LogClass.Cpu, "Vectored exception handler removed.");
            }

            _faultHandler = null;
        }

        /// <summary>
        /// The actual VEH callback. Called by Windows for any unhandled exception.
        /// We only handle EXCEPTION_ACCESS_VIOLATION and only if the fault address
        /// belongs to the emulator's managed memory regions.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int VectoredExceptionHandler(WindowsNative.EXCEPTION_POINTERS* exceptionInfo)
        {
            if (exceptionInfo == null || exceptionInfo->ExceptionRecord == nint.Zero)
                return (int)WindowsNative.EXCEPTION_CONTINUE_SEARCH;

            WindowsNative.EXCEPTION_RECORD* record =
                (WindowsNative.EXCEPTION_RECORD*)exceptionInfo->ExceptionRecord;

            // Only handle access violations
            if (record->ExceptionCode != WindowsNative.EXCEPTION_ACCESS_VIOLATION)
                return (int)WindowsNative.EXCEPTION_CONTINUE_SEARCH;

            // ExceptionInformation[1] contains the virtual address that caused the fault
            ulong faultAddress = (ulong)record->ExceptionInformation1;

            // Attempt to handle via the emulator's fault handler
            FaultHandler handler = _faultHandler;
            if (handler != null)
            {
                try
                {
                    if (handler(faultAddress))
                    {
                        // Fault was handled (e.g., lazy page mapping), continue execution
                        return unchecked((int)WindowsNative.EXCEPTION_CONTINUE_EXECUTION);
                    }
                }
                catch
                {
                    // Don't let exceptions escape from VEH
                }
            }

            // Not our fault, let other handlers deal with it
            return (int)WindowsNative.EXCEPTION_CONTINUE_SEARCH;
        }

        /// <summary>
        /// Attempts to handle an exception programmatically (for testing/direct use).
        /// </summary>
        public static bool TryHandleFault(ulong faultAddress)
        {
            return _faultHandler?.Invoke(faultAddress) ?? false;
        }
    }
}
