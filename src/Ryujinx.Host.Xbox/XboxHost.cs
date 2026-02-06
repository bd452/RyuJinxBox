using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Audio;
using Ryujinx.Host.Xbox.Input;
using System;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// The main Xbox host that ties together all platform-specific subsystems.
    /// Owns the lifecycle, input, audio, filesystem, and logging backends.
    ///
    /// The emulator core runs inside a managed execution loop owned by this host.
    /// Ryujinx believes it is running on a normal Vulkan + Windows-like host;
    /// the Xbox-specific work is concentrated here and in Ryujinx.Graphics.Xbox.
    /// </summary>
    public sealed class XboxHost : IDisposable
    {
        private bool _isDisposed;

        /// <summary>
        /// Application lifecycle manager for Xbox suspend/resume/activate.
        /// </summary>
        public XboxApplicationLifecycle Lifecycle { get; }

        /// <summary>
        /// Xbox sandbox-safe filesystem mapping.
        /// </summary>
        public XboxFileSystem FileSystem { get; }

        /// <summary>
        /// Performance tuning profile for the detected Xbox hardware.
        /// </summary>
        public XboxPerformanceProfile PerformanceProfile { get; }

        /// <summary>
        /// XAudio2-based audio driver.
        /// </summary>
        public XAudio2HardwareDeviceDriver AudioDriver { get; private set; }

        /// <summary>
        /// Xbox gamepad input driver.
        /// </summary>
        public XboxGamepadDriver GamepadDriver { get; private set; }

        /// <summary>
        /// Xbox-safe logging target.
        /// </summary>
        public XboxLogTarget LogTarget { get; private set; }

        /// <summary>
        /// Creates a new Xbox host with the specified storage paths.
        /// </summary>
        /// <param name="localFolder">Path to ApplicationData.LocalFolder (persistent).</param>
        /// <param name="temporaryFolder">Path to ApplicationData.TemporaryFolder (temporary).</param>
        public XboxHost(string localFolder, string temporaryFolder)
        {
            Lifecycle = new XboxApplicationLifecycle();
            FileSystem = new XboxFileSystem(localFolder, temporaryFolder);

            // Detect hardware and create appropriate tuning profile
            XboxHardwareModel model = XboxPerformanceProfile.DetectHardware();
            PerformanceProfile = XboxPerformanceProfile.Create(model);

            Logger.Info?.Print(LogClass.Application,
                $"Xbox host created. Hardware: {PerformanceProfile.HardwareModel}, " +
                $"Target resolution: {PerformanceProfile.TargetResolutionWidth}x{PerformanceProfile.TargetResolutionHeight}");
        }

        /// <summary>
        /// Initializes all Xbox host subsystems.
        /// Should be called during OnActivated before the emulator core starts.
        /// </summary>
        public void Initialize()
        {
            Logger.Info?.Print(LogClass.Application, "Initializing Xbox host subsystems...");

            // 1. Ensure filesystem directories exist
            FileSystem.EnsureDirectoriesExist();

            // 2. Initialize logging
            LogTarget = new XboxLogTarget(FileSystem.LogsPath);
            Logger.Info?.Print(LogClass.Application, $"Xbox log target created at: {FileSystem.LogsPath}");

            // 3. Initialize audio
            AudioDriver = new XAudio2HardwareDeviceDriver();

            // 4. Initialize input
            GamepadDriver = new XboxGamepadDriver();

            // 5. Install exception handler for JIT
            XboxExceptionHandler.Install(HandleJitFault);

            // 6. Wire lifecycle events
            Lifecycle.OnSuspending += OnSuspending;
            Lifecycle.OnResuming += OnResuming;

            Logger.Info?.Print(LogClass.Application, "Xbox host subsystems initialized.");
        }

        private void OnSuspending()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox host handling suspension...");

            // Pause audio to free device resources during suspension
            // The emulator core should also pause via Lifecycle.WaitIfSuspended()
        }

        private void OnResuming()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox host handling resume...");

            // Re-acquire audio devices, restore GPU state, etc.
        }

        private bool HandleJitFault(ulong faultAddress)
        {
            // This would be wired into the Ryujinx CPU emulation fault handling.
            // The emulator's memory manager checks if this is a legitimate
            // emulated memory access that needs to be handled (e.g., lazy page mapping).
            Logger.Warning?.Print(LogClass.Cpu, $"JIT fault at address 0x{faultAddress:X16}");
            return false;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            Logger.Info?.Print(LogClass.Application, "Disposing Xbox host...");

            XboxExceptionHandler.Uninstall();

            Lifecycle.OnSuspending -= OnSuspending;
            Lifecycle.OnResuming -= OnResuming;

            GamepadDriver?.Dispose();
            AudioDriver?.Dispose();
            LogTarget?.Dispose();
            Lifecycle?.Dispose();

            Logger.Info?.Print(LogClass.Application, "Xbox host disposed.");
        }
    }
}
