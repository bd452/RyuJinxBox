using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Audio;
using Ryujinx.Host.Xbox.Input;
using Ryujinx.Host.Xbox.Memory;
using System;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// The main Xbox host that ties together all platform-specific subsystems.
    ///
    /// The emulator core runs inside a managed execution loop owned by this host.
    /// Ryujinx believes it is running on a normal Vulkan + Windows-like host.
    /// </summary>
    public sealed class XboxHost : IDisposable
    {
        private bool _isDisposed;

        public XboxApplicationLifecycle Lifecycle { get; }
        public XboxFileSystem FileSystem { get; }
        public XboxPerformanceProfile PerformanceProfile { get; }
        public XAudio2HardwareDeviceDriver AudioDriver { get; private set; }
        public XboxGamepadDriver GamepadDriver { get; private set; }
        public XboxLogTarget LogTarget { get; private set; }
        public XboxMemoryAllocator MemoryAllocator { get; private set; }

        public XboxHost(string localFolder, string temporaryFolder)
        {
            Lifecycle = new XboxApplicationLifecycle();
            FileSystem = new XboxFileSystem(localFolder, temporaryFolder);

            XboxHardwareModel model = XboxPerformanceProfile.DetectHardware();
            PerformanceProfile = XboxPerformanceProfile.Create(model);

            Logger.Info?.Print(LogClass.Application,
                $"Xbox host created. Hardware: {PerformanceProfile.HardwareModel}, " +
                $"Target: {PerformanceProfile.TargetResolutionWidth}x{PerformanceProfile.TargetResolutionHeight}");
        }

        /// <summary>
        /// Initializes all Xbox host subsystems.
        /// </summary>
        public void Initialize()
        {
            Logger.Info?.Print(LogClass.Application, "Initializing Xbox host subsystems...");

            FileSystem.EnsureDirectoriesExist();

            LogTarget = new XboxLogTarget(FileSystem.LogsPath);
            Logger.AddTarget(LogTarget);

            AudioDriver = new XAudio2HardwareDeviceDriver();
            GamepadDriver = new XboxGamepadDriver();

            MemoryAllocator = new XboxMemoryAllocator();

            if (OperatingSystem.IsWindows())
            {
                XboxExceptionHandler.Install(HandleJitFault);
            }

            Lifecycle.OnSuspending += OnSuspending;
            Lifecycle.OnResuming += OnResuming;

            Logger.Info?.Print(LogClass.Application, "Xbox host subsystems initialized.");
        }

        private void OnSuspending()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox host suspending...");
        }

        private void OnResuming()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox host resuming...");
        }

        private bool HandleJitFault(ulong faultAddress)
        {
            Logger.Warning?.Print(LogClass.Cpu, $"JIT fault at 0x{faultAddress:X16}");
            return false;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Logger.Info?.Print(LogClass.Application, "Disposing Xbox host...");

            if (OperatingSystem.IsWindows())
            {
                XboxExceptionHandler.Uninstall();
            }

            Lifecycle.OnSuspending -= OnSuspending;
            Lifecycle.OnResuming -= OnResuming;

            MemoryAllocator?.Dispose();
            GamepadDriver?.Dispose();
            AudioDriver?.Dispose();
            LogTarget?.Dispose();
            Lifecycle?.Dispose();
        }
    }
}
