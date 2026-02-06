using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox;
using System;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Top-level initializer for the Xbox graphics stack.
    ///
    /// Architecture:
    ///   Ryujinx Vulkan Renderer (SPIR-V)
    ///         ↓
    ///   DXVK (Vulkan → D3D12 translation)
    ///         ↓
    ///   Xbox D3D12 / GameCore
    ///
    /// Ryujinx believes it is running on a normal Vulkan host.
    /// All Xbox-specific graphics work is concentrated here.
    /// </summary>
    public sealed class XboxGraphicsInitializer : IDisposable
    {
        private readonly XboxFileSystem _fileSystem;
        private readonly XboxPerformanceProfile _profile;
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// The pipeline cache manager for this session.
        /// </summary>
        public XboxPipelineCacheManager PipelineCacheManager { get; private set; }

        /// <summary>
        /// Creates a new Xbox graphics initializer.
        /// </summary>
        /// <param name="fileSystem">The Xbox filesystem for cache path resolution.</param>
        /// <param name="profile">The performance profile for the current hardware.</param>
        public XboxGraphicsInitializer(XboxFileSystem fileSystem, XboxPerformanceProfile profile)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        /// <summary>
        /// Initializes the entire Xbox graphics stack in the correct order.
        ///
        /// Order of operations:
        /// 1. Configure DXVK environment
        /// 2. Bootstrap DXVK library
        /// 3. Install Vulkan loader overrides
        /// 4. Initialize pipeline cache
        /// 5. Apply hardware-specific overrides
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                return;
            }

            Logger.Info?.Print(LogClass.Gpu, "Initializing Xbox graphics stack...");

            // Step 1: Configure DXVK environment variables
            DxvkConfiguration.Apply(_fileSystem.ShaderCachePath, _fileSystem.PipelineCachePath);

            // Step 2: Apply hardware-specific overrides
            switch (_profile.HardwareModel)
            {
                case XboxHardwareModel.SeriesS:
                    DxvkConfiguration.ApplySeriesSOverrides();
                    break;
                case XboxHardwareModel.SeriesX:
                    DxvkConfiguration.ApplySeriesXOverrides();
                    break;
            }

            // Step 3: Bootstrap DXVK
            DxvkBootstrap.Initialize();

            // Step 4: Install Vulkan loader overrides
            XboxVulkanLoader.Install();

            // Step 5: Initialize pipeline cache manager
            PipelineCacheManager = new XboxPipelineCacheManager(
                _fileSystem.PipelineCachePath,
                _profile.MaxShaderCacheEntries,
                _profile.MaxPipelineCacheSize);

            PipelineCacheManager.LoadFromDisk();

            _isInitialized = true;

            Logger.Info?.Print(LogClass.Gpu,
                $"Xbox graphics stack initialized. Hardware: {_profile.HardwareModel}, " +
                $"DXVK: {DxvkBootstrap.IsInitialized}, Vulkan override: {XboxVulkanLoader.IsActive}");
        }

        /// <summary>
        /// Persists all caches to disk. Should be called during suspension and shutdown.
        /// </summary>
        public void FlushCaches()
        {
            PipelineCacheManager?.FlushToDisk();
            Logger.Info?.Print(LogClass.Gpu, "Xbox graphics caches flushed.");
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            PipelineCacheManager?.Dispose();
            DxvkBootstrap.Shutdown();

            Logger.Info?.Print(LogClass.Gpu, "Xbox graphics stack disposed.");
        }
    }
}
