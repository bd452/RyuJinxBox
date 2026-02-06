using LibHac.Tools.FsSystem;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Graphics.Xbox;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.Host.Xbox.Audio;
using Ryujinx.Host.Xbox.Input;
using Ryujinx.Host.Xbox.UI;
using Ryujinx.Input.HLE;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Threading;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Xbox entry point for Ryujinx.
    /// Boots the emulator without SDL3 or Avalonia, using Xbox-native backends.
    ///
    /// On Xbox, this would be called from a UWP/GameCore App.OnActivated handler.
    /// The lifecycle is:
    ///   1. Initialize Xbox host subsystems
    ///   2. Initialize Vulkan-over-D3D12 graphics
    ///   3. Initialize emulation context
    ///   4. Load ROM
    ///   5. Run emulation loop (respecting suspend/resume)
    /// </summary>
    public static class XboxProgram
    {
        private static XboxHost _host;
        private static XboxGraphicsInitializer _graphicsInit;
        private static Switch _emulationContext;
        private static VirtualFileSystem _virtualFileSystem;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static InputManager _inputManager;

        /// <summary>
        /// Main entry point for Xbox. Call this from your UWP App.OnActivated or GameCore entry.
        /// </summary>
        /// <param name="localFolder">Path to ApplicationData.LocalFolder.</param>
        /// <param name="temporaryFolder">Path to ApplicationData.TemporaryFolder.</param>
        /// <param name="romPath">Path to the ROM to load.</param>
        public static void Run(string localFolder, string temporaryFolder, string romPath)
        {
            try
            {
                // 1. Initialize Xbox host
                _host = new XboxHost(localFolder, temporaryFolder);
                _host.Initialize();

                Logger.Notice.Print(LogClass.Application, "Ryujinx Xbox starting...");

                // 2. Initialize graphics (Vulkan over D3D12)
                var profile = _host.PerformanceProfile;
                var fs = _host.FileSystem;
                _graphicsInit = new XboxGraphicsInitializer(
                    fs.ShaderCachePath,
                    fs.PipelineCachePath,
                    profile.MaxShaderCacheEntries,
                    profile.MaxPipelineCacheSize,
                    profile.HardwareModel == XboxHardwareModel.SeriesS);
                _graphicsInit.Initialize();

                // 3. Set up data directories
                AppDataManager.Initialize(localFolder);

                // 4. Initialize HLE infrastructure
                _virtualFileSystem = VirtualFileSystem.CreateInstance();
                _libHacHorizonManager = new LibHacHorizonManager();
                _libHacHorizonManager.InitializeFsServer(_virtualFileSystem);
                _libHacHorizonManager.InitializeArpServer();
                _libHacHorizonManager.InitializeBcatServer();
                _libHacHorizonManager.InitializeSystemClients();

                _contentManager = new ContentManager(_virtualFileSystem);
                _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient);
                _userChannelPersistence = new UserChannelPersistence();

                // 5. Initialize input - Xbox gamepad driver (no keyboard on Xbox)
                _inputManager = new InputManager(_host.GamepadDriver, _host.GamepadDriver);

                // 6. Create Vulkan renderer
                Vk api = Vk.GetApi();
                IRenderer renderer = new VulkanRenderer(
                    api,
                    (instance, vk) => default,  // No window surface needed - Xbox uses direct presentation
                    () => ["VK_KHR_surface", "VK_KHR_win32_surface"],
                    string.Empty);

                // 7. Create emulation context
                GraphicsConfig.EnableShaderCache = true;
                GraphicsConfig.ResScale = _host.PerformanceProfile.HardwareModel == XboxHardwareModel.SeriesX ? 1 : 1;

                var hleConfig = new HleConfiguration(
                    MemoryConfiguration.MemoryConfiguration4GiB,
                    SystemLanguage.AmericanEnglish,
                    RegionCode.USA,
                    VSyncMode.Switch,
                    true,   // enableDockedMode
                    true,   // enablePtc
                    ITickSource.RealityTickScalar,
                    false,  // enableInternetAccess
                    IntegrityCheckLevel.None,
                    0,      // fsGlobalAccessLogMode
                    0,      // systemTimeOffset
                    null,   // timeZone
                    MemoryManagerMode.HostMappedUnsafe,
                    false,  // ignoreMissingServices
                    AspectRatio.Fixed16x9,
                    1.0f,   // audioVolume
                    false,  // useHypervisor (not available on Xbox)
                    null,   // multiplayerLanInterfaceId
                    Common.Configuration.Multiplayer.MultiplayerMode.Disabled,
                    false,
                    string.Empty,
                    string.Empty,
                    false,  // enableGdbStub
                    0,      // gdbStubPort
                    false,  // debuggerSuspendOnStart
                    0       // customVSyncInterval
                ).Configure(
                    _virtualFileSystem,
                    _libHacHorizonManager,
                    _contentManager,
                    _accountManager,
                    _userChannelPersistence,
                    renderer,
                    _host.AudioDriver,
                    new XboxUIHandler()
                );

                _emulationContext = new Switch(hleConfig);

                // 8. Load ROM
                if (!LoadRom(romPath))
                {
                    Logger.Error?.Print(LogClass.Application, $"Failed to load ROM: {romPath}");
                    Shutdown();
                    return;
                }

                Logger.Notice.Print(LogClass.Application, "ROM loaded successfully. Starting emulation...");

                // 9. Activate lifecycle
                _host.Lifecycle.HandleActivated();

                // 10. Run emulation loop
                RunEmulationLoop();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Fatal error: {ex}");
            }
            finally
            {
                Shutdown();
            }
        }

        private static bool LoadRom(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Logger.Error?.Print(LogClass.Application, $"ROM not found: {path}");
                return false;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();

            return extension switch
            {
                ".xci" => _emulationContext.LoadXci(path),
                ".nsp" or ".pfs0" => _emulationContext.LoadNsp(path),
                ".nca" => _emulationContext.LoadNca(path),
                ".nro" or ".nso" => _emulationContext.LoadProgram(path),
                _ => TryLoadAsDirectory(path),
            };
        }

        private static bool TryLoadAsDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                string[] romFsFiles = Directory.GetFiles(path, "*.istorage");
                if (romFsFiles.Length == 0)
                    romFsFiles = Directory.GetFiles(path, "*.romfs");

                return romFsFiles.Length > 0
                    ? _emulationContext.LoadCart(path, romFsFiles[0])
                    : _emulationContext.LoadCart(path);
            }

            return _emulationContext.LoadProgram(path);
        }

        /// <summary>
        /// Main emulation loop. Respects Xbox suspend/resume lifecycle.
        /// </summary>
        private static void RunEmulationLoop()
        {
            CancellationToken shutdownToken = _host.Lifecycle.ShutdownToken;

            // Start GPU and CPU threads
            Thread gpuThread = new(GpuLoop) { Name = "Xbox.GpuThread", IsBackground = true };
            gpuThread.Start();

            // NpadManager update loop
            var npadManager = _inputManager.CreateNpadManager();

            while (!shutdownToken.IsCancellationRequested)
            {
                // Block if suspended
                _host.Lifecycle.WaitIfSuspended();

                if (shutdownToken.IsCancellationRequested)
                    break;

                // Update input
                npadManager.Update();

                // ~60 FPS input polling
                Thread.Sleep(16);
            }

            Logger.Notice.Print(LogClass.Application, "Emulation loop ended.");
        }

        private static void GpuLoop()
        {
            CancellationToken shutdownToken = _host.Lifecycle.ShutdownToken;

            while (!shutdownToken.IsCancellationRequested)
            {
                _host.Lifecycle.WaitIfSuspended();

                if (shutdownToken.IsCancellationRequested)
                    break;

                // GPU frame processing is handled by the GPU context internally
                Thread.Sleep(1);
            }
        }

        private static void Shutdown()
        {
            Logger.Notice.Print(LogClass.Application, "Shutting down...");

            _graphicsInit?.FlushCaches();
            _emulationContext?.Dispose();
            _inputManager?.Dispose();
            _graphicsInit?.Dispose();
            _host?.Dispose();

            Logger.Notice.Print(LogClass.Application, "Shutdown complete.");
            Logger.Shutdown();
        }
    }
}
