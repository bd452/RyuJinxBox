using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Integration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
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
using Ryujinx.Host.Xbox.Native;
using Ryujinx.Host.Xbox.UI;
using Ryujinx.Input.HLE;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Switch = Ryujinx.HLE.Switch;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Xbox entry point for Ryujinx.
    /// Boots the emulator using Xbox-native backends:
    ///   Audio:    XAudio2 (COM interop)
    ///   Input:    GameInput API
    ///   Graphics: Vulkan through D3D12-backed ICD
    ///   Memory:   VirtualAllocFromApp with W^X
    ///   Window:   CoreWindow HWND → VK_KHR_win32_surface
    /// </summary>
    public static class XboxProgram
    {
        private const int TargetFps = 60;

        private static XboxHost _host;
        private static XboxGraphicsInitializer _graphicsInit;
        private static Switch _emulationContext;
        private static VirtualFileSystem _virtualFileSystem;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static InputManager _inputManager;
        private static NpadManager _npadManager;

        private static volatile bool _isActive;
        private static volatile bool _isStopped;
        private static readonly CancellationTokenSource _gpuCancellationTokenSource = new();
        private static readonly ManualResetEvent _gpuDoneEvent = new(false);
        private static readonly ManualResetEvent _exitEvent = new(false);
        private static readonly Stopwatch _chrono = new();
        private static readonly long _ticksPerFrame = Stopwatch.Frequency / TargetFps;
        private static long _ticks;

        // Window dimensions - Series S targets 720p, Series X targets 1080p
        private static int _width;
        private static int _height;

        /// <summary>
        /// Main entry point. Called from UWP App.OnActivated or GameCore main.
        /// </summary>
        /// <param name="localFolder">ApplicationData.LocalFolder.Path</param>
        /// <param name="temporaryFolder">ApplicationData.TemporaryFolder.Path</param>
        /// <param name="romPath">Full path to the ROM file within the sandbox.</param>
        public static void Run(string localFolder, string temporaryFolder, string romPath)
        {
            try
            {
                InitializeHost(localFolder, temporaryFolder);
                InitializeGraphics();
                InitializeEmulation();
                InitializeInput();

                if (!LoadRom(romPath))
                {
                    Logger.Error?.Print(LogClass.Application, $"Failed to load: {romPath}");
                    return;
                }

                _host.Lifecycle.HandleActivated();
                Execute();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Fatal: {ex}");
            }
            finally
            {
                Shutdown();
            }
        }

        private static void InitializeHost(string localFolder, string temporaryFolder)
        {
            _host = new XboxHost(localFolder, temporaryFolder);
            _host.Initialize();

            _width = _host.PerformanceProfile.TargetResolutionWidth;
            _height = _host.PerformanceProfile.TargetResolutionHeight;

            Logger.Notice.Print(LogClass.Application,
                $"Ryujinx Xbox | {_host.PerformanceProfile.HardwareModel} | {_width}x{_height}");
        }

        private static void InitializeGraphics()
        {
            var profile = _host.PerformanceProfile;
            var fs = _host.FileSystem;

            _graphicsInit = new XboxGraphicsInitializer(
                fs.ShaderCachePath,
                fs.PipelineCachePath,
                profile.MaxShaderCacheEntries,
                profile.MaxPipelineCacheSize,
                profile.HardwareModel == XboxHardwareModel.SeriesS);

            _graphicsInit.Initialize();
        }

        private static void InitializeEmulation()
        {
            AppDataManager.Initialize(_host.FileSystem.LocalFolder);

            _virtualFileSystem = VirtualFileSystem.CreateInstance();
            _libHacHorizonManager = new LibHacHorizonManager();
            _libHacHorizonManager.InitializeFsServer(_virtualFileSystem);
            _libHacHorizonManager.InitializeArpServer();
            _libHacHorizonManager.InitializeBcatServer();
            _libHacHorizonManager.InitializeSystemClients();

            _contentManager = new ContentManager(_virtualFileSystem);
            _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient);
            _userChannelPersistence = new UserChannelPersistence();

            // Create Vulkan renderer with Xbox surface
            Vk api = Vk.GetApi();
            IRenderer renderer = new VulkanRenderer(
                api,
                CreateVulkanSurface,
                GetRequiredVulkanExtensions,
                string.Empty);

            // Enable threading for GPU pipeline
            renderer = renderer.TryMakeThreaded(BackendThreading.Auto);

            GraphicsConfig.EnableShaderCache = true;
            GraphicsConfig.EnableMacroHLE = true;

            var hleConfig = new HleConfiguration(
                MemoryConfiguration.MemoryConfiguration4GiB,
                SystemLanguage.AmericanEnglish,
                RegionCode.USA,
                VSyncMode.Switch,
                true,   // dockedMode
                true,   // ptc
                ITickSource.RealityTickScalar,
                false,  // internet
                IntegrityCheckLevel.None,
                0,      // fsLogMode
                0,      // timeOffset
                null,   // timezone
                MemoryManagerMode.HostMappedUnsafe,
                false,  // ignoreMissingServices
                AspectRatio.Fixed16x9,
                1.0f,   // audioVolume
                false,  // hypervisor - not available on Xbox
                null,   // lanInterfaceId
                Common.Configuration.Multiplayer.MultiplayerMode.Disabled,
                false,
                string.Empty,
                string.Empty,
                false,  // gdb
                0,
                false,
                0
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
        }

        /// <summary>
        /// Creates a Vulkan surface from the Xbox CoreWindow HWND.
        /// Uses VK_KHR_win32_surface to bind Vulkan presentation to the Xbox window.
        /// </summary>
        private static unsafe SurfaceKHR CreateVulkanSurface(Instance instance, Vk api)
        {
            // The surface creation callback is invoked during VulkanRenderer.Initialize(),
            // which runs on the render thread. The CoreWindow HWND should exist by now,
            // but on Xbox the window might not be fully ready. Retry briefly.
            nint hwnd = nint.Zero;
            nint hinstance = CoreWindowNative.GetHInstance();

            for (int attempt = 0; attempt < 50; attempt++)
            {
                hwnd = CoreWindowNative.GetXboxWindowHandle();
                if (hwnd != nint.Zero)
                    break;

                Thread.Sleep(100);
            }

            if (hwnd == nint.Zero)
            {
                Logger.Error?.Print(LogClass.Gpu, "No window handle available for Vulkan surface creation after retries.");
                return default;
            }

            if (!api.TryGetInstanceExtension(instance, out KhrWin32Surface win32SurfaceExt))
            {
                Logger.Error?.Print(LogClass.Gpu, "VK_KHR_win32_surface extension not available.");
                return default;
            }

            Win32SurfaceCreateInfoKHR createInfo = new()
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hinstance = hinstance,
                Hwnd = hwnd,
            };

            Result result = win32SurfaceExt.CreateWin32Surface(instance, &createInfo, null, out SurfaceKHR surface);

            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Gpu, $"vkCreateWin32SurfaceKHR failed: {result}");
                return default;
            }

            Logger.Info?.Print(LogClass.Gpu, $"Vulkan Win32 surface created. HWND=0x{hwnd:X}, Surface=0x{surface.Handle:X}");
            return surface;
        }

        private static string[] GetRequiredVulkanExtensions()
        {
            return ["VK_KHR_surface", "VK_KHR_win32_surface"];
        }

        private static void InitializeInput()
        {
            _inputManager = new InputManager(_host.GamepadDriver, _host.GamepadDriver);
            _npadManager = _inputManager.CreateNpadManager();

            // Configure Player 1 with default Xbox controller mapping
            var inputConfigs = new List<InputConfig>
            {
                CreateDefaultXboxControllerConfig(PlayerIndex.Player1),
            };

            _npadManager.Initialize(_emulationContext, inputConfigs, false, false);
        }

        /// <summary>
        /// Creates a default input configuration mapping Xbox controller to Switch Pro Controller.
        /// </summary>
        private static InputConfig CreateDefaultXboxControllerConfig(PlayerIndex playerIndex)
        {
            // Get the first connected gamepad ID, or use a placeholder
            ReadOnlySpan<string> ids = _host.GamepadDriver.GamepadsIds;
            string gamepadId = ids.Length > 0 ? ids[0] : "0";

            return new StandardControllerInputConfig
            {
                Version = InputConfig.CurrentVersion,
                Backend = InputBackendType.GamepadSDL2, // Closest match for config format
                Id = gamepadId,
                ControllerType = ControllerType.ProController,
                PlayerIndex = playerIndex,
                DeadzoneLeft = 0.10f,
                DeadzoneRight = 0.10f,
                RangeLeft = 1.0f,
                RangeRight = 1.0f,
                TriggerThreshold = 0.50f,
                LeftJoycon = new LeftJoyconCommonConfig<GamepadInputId>
                {
                    ButtonMinus = GamepadInputId.Back,
                    ButtonL = GamepadInputId.LeftShoulder,
                    ButtonZl = GamepadInputId.LeftTrigger,
                    ButtonSl = GamepadInputId.Unbound,
                    ButtonSr = GamepadInputId.Unbound,
                    DpadUp = GamepadInputId.DpadUp,
                    DpadDown = GamepadInputId.DpadDown,
                    DpadLeft = GamepadInputId.DpadLeft,
                    DpadRight = GamepadInputId.DpadRight,
                },
                LeftJoyconStick = new JoyconConfigControllerStick<GamepadInputId, Common.Configuration.Hid.Controller.StickInputId>
                {
                    Joystick = Common.Configuration.Hid.Controller.StickInputId.Left,
                    StickButton = GamepadInputId.LeftStick,
                    InvertStickX = false,
                    InvertStickY = false,
                    Rotate90CW = false,
                },
                RightJoycon = new RightJoyconCommonConfig<GamepadInputId>
                {
                    ButtonPlus = GamepadInputId.Start,
                    ButtonR = GamepadInputId.RightShoulder,
                    ButtonZr = GamepadInputId.RightTrigger,
                    ButtonSl = GamepadInputId.Unbound,
                    ButtonSr = GamepadInputId.Unbound,
                    ButtonA = GamepadInputId.B, // Xbox B → Switch A (Nintendo layout)
                    ButtonB = GamepadInputId.A, // Xbox A → Switch B
                    ButtonX = GamepadInputId.Y, // Xbox Y → Switch X
                    ButtonY = GamepadInputId.X, // Xbox X → Switch Y
                },
                RightJoyconStick = new JoyconConfigControllerStick<GamepadInputId, Common.Configuration.Hid.Controller.StickInputId>
                {
                    Joystick = Common.Configuration.Hid.Controller.StickInputId.Right,
                    StickButton = GamepadInputId.RightStick,
                    InvertStickX = false,
                    InvertStickY = false,
                    Rotate90CW = false,
                },
                Motion = new MotionConfigController
                {
                    EnableMotion = false,
                    MotionBackend = MotionInputBackendType.GamepadDriver,
                    Sensitivity = 100,
                    GyroDeadzone = 1,
                },
                Rumble = new RumbleConfigController
                {
                    EnableRumble = true,
                    StrongRumble = 1f,
                    WeakRumble = 1f,
                },
            };
        }

        /// <summary>
        /// Main execution loop. Mirrors the headless WindowBase architecture:
        /// - Render thread: GPU init → shader cache → frame processing → present
        /// - Main thread: input polling → NpadManager update
        /// </summary>
        private static void Execute()
        {
            _chrono.Restart();
            _isActive = true;

            // Render thread handles GPU initialization and frame loop
            Thread renderThread = new(RenderLoop) { Name = "Xbox.RenderThread" };
            renderThread.Start();

            // Main thread handles input
            MainLoop();

            _exitEvent.Set();
        }

        /// <summary>
        /// GPU render loop. Runs on a dedicated thread.
        /// Initializes the renderer, loads shader cache, then enters the frame loop.
        /// </summary>
        private static void RenderLoop()
        {
            IRenderer renderer = _emulationContext.Gpu.Renderer;

            if (renderer is ThreadedRenderer tr)
                renderer = tr.BaseRenderer;

            renderer.Initialize(GraphicsDebugLevel.None);
            renderer.Window.SetSize(_width, _height);

            string gpuDriver = renderer.GetHardwareInfo().GpuDriver;
            Logger.Notice.Print(LogClass.Gpu, $"GPU: {gpuDriver}");

            _emulationContext.Gpu.Renderer.RunLoop(() =>
            {
                _emulationContext.Gpu.SetGpuThread();
                _emulationContext.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                while (_isActive)
                {
                    if (_isStopped)
                        return;

                    // Respect Xbox suspend/resume
                    _host.Lifecycle.WaitIfSuspended();

                    if (_host.Lifecycle.ShutdownToken.IsCancellationRequested)
                        return;

                    _ticks += _chrono.ElapsedTicks;
                    _chrono.Restart();

                    if (_emulationContext.WaitFifo())
                    {
                        _emulationContext.Statistics.RecordFifoStart();
                        _emulationContext.ProcessFrame();
                        _emulationContext.Statistics.RecordFifoEnd();
                    }

                    while (_emulationContext.ConsumeFrameAvailable())
                    {
                        // Present frame - on Xbox the ICD handles swapchain presentation
                        _emulationContext.PresentFrame(() => { });
                    }

                    if (_ticks >= _ticksPerFrame)
                    {
                        _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame);
                    }
                }

                // Flush any remaining GPU commands
                if (_emulationContext.Gpu.Renderer is ThreadedRenderer threaded)
                    threaded.FlushThreadedCommands();

                _gpuDoneEvent.Set();
            });
        }

        /// <summary>
        /// Main loop. Runs on the main thread. Handles input polling.
        /// </summary>
        private static void MainLoop()
        {
            while (_isActive)
            {
                if (_isStopped)
                    break;

                // Respect Xbox suspend
                _host.Lifecycle.WaitIfSuspended();

                if (_host.Lifecycle.ShutdownToken.IsCancellationRequested)
                {
                    _isActive = false;
                    break;
                }

                // Poll input and update HID state
                _npadManager.Update();
                _emulationContext.Hid.DebugPad.Update();

                Thread.Sleep(1);
            }
        }

        private static bool LoadRom(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                Logger.Error?.Print(LogClass.Application, "No ROM path provided.");
                return false;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Logger.Error?.Print(LogClass.Application, $"ROM not found: {path}");
                return false;
            }

            Logger.Notice.Print(LogClass.Application, $"Loading: {path}");

            string ext = Path.GetExtension(path).ToLowerInvariant();

            bool success = ext switch
            {
                ".xci" => _emulationContext.LoadXci(path),
                ".nsp" or ".pfs0" => _emulationContext.LoadNsp(path),
                ".nca" => _emulationContext.LoadNca(path),
                ".nro" or ".nso" => _emulationContext.LoadProgram(path),
                _ => Directory.Exists(path) ? LoadFromDirectory(path) : _emulationContext.LoadProgram(path),
            };

            if (success)
                Logger.Notice.Print(LogClass.Application, "ROM loaded.");

            return success;
        }

        private static bool LoadFromDirectory(string path)
        {
            string[] romFsFiles = Directory.GetFiles(path, "*.istorage");
            if (romFsFiles.Length == 0)
                romFsFiles = Directory.GetFiles(path, "*.romfs");

            return romFsFiles.Length > 0
                ? _emulationContext.LoadCart(path, romFsFiles[0])
                : _emulationContext.LoadCart(path);
        }

        private static void Shutdown()
        {
            Logger.Notice.Print(LogClass.Application, "Shutting down...");

            _isStopped = true;
            _isActive = false;
            _gpuCancellationTokenSource.Cancel();

            _graphicsInit?.FlushCaches();

            _npadManager?.Dispose();
            _inputManager?.Dispose();
            _emulationContext?.Dispose();
            _graphicsInit?.Dispose();
            _host?.Dispose();

            _gpuDoneEvent.Dispose();
            _exitEvent.Dispose();
            _gpuCancellationTokenSource.Dispose();

            Logger.Notice.Print(LogClass.Application, "Shutdown complete.");
            Logger.Shutdown();
        }
    }
}
