using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Top-level initializer for the Xbox graphics stack.
    ///
    /// Architecture:
    ///   Ryujinx Vulkan Renderer (SPIR-V)
    ///         ↓
    ///   Vulkan ICD (D3D12-backed, e.g. Mesa Dozen)
    ///         ↓
    ///   Xbox D3D12 / GameCore
    /// </summary>
    public sealed class XboxGraphicsInitializer : IDisposable
    {
        private readonly string _shaderCachePath;
        private readonly string _pipelineCachePath;
        private readonly int _maxShaderCacheEntries;
        private readonly long _maxPipelineCacheSize;
        private readonly bool _isSeriesS;
        private bool _isInitialized;
        private bool _isDisposed;

        public XboxPipelineCacheManager PipelineCacheManager { get; private set; }

        /// <summary>
        /// Creates a new Xbox graphics initializer.
        /// </summary>
        /// <param name="shaderCachePath">Path for shader cache storage.</param>
        /// <param name="pipelineCachePath">Path for pipeline cache storage.</param>
        /// <param name="maxShaderCacheEntries">Max shader cache entries before LRU eviction.</param>
        /// <param name="maxPipelineCacheSize">Max pipeline cache size in bytes.</param>
        /// <param name="isSeriesS">True if running on Series S (apply performance overrides).</param>
        public XboxGraphicsInitializer(
            string shaderCachePath,
            string pipelineCachePath,
            int maxShaderCacheEntries,
            long maxPipelineCacheSize,
            bool isSeriesS)
        {
            _shaderCachePath = shaderCachePath ?? throw new ArgumentNullException(nameof(shaderCachePath));
            _pipelineCachePath = pipelineCachePath ?? throw new ArgumentNullException(nameof(pipelineCachePath));
            _maxShaderCacheEntries = maxShaderCacheEntries;
            _maxPipelineCacheSize = maxPipelineCacheSize;
            _isSeriesS = isSeriesS;
        }

        /// <summary>
        /// Initializes the entire Xbox graphics stack in the correct order.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            Logger.Info?.Print(LogClass.Gpu, "Initializing Xbox graphics stack...");

            // Step 1: Configure DXVK/ICD environment
            DxvkConfiguration.Apply(_shaderCachePath, _pipelineCachePath);

            // Step 2: Apply hardware-specific overrides
            if (_isSeriesS)
                DxvkConfiguration.ApplySeriesSOverrides();
            else
                DxvkConfiguration.ApplySeriesXOverrides();

            // Step 3: Bootstrap the Vulkan-over-D3D12 ICD
            DxvkBootstrap.Initialize();

            // Step 4: Install Vulkan loader overrides
            XboxVulkanLoader.Install();

            // Step 5: Initialize pipeline cache
            PipelineCacheManager = new XboxPipelineCacheManager(
                _pipelineCachePath,
                _maxShaderCacheEntries,
                _maxPipelineCacheSize);
            PipelineCacheManager.LoadFromDisk();

            _isInitialized = true;

            Logger.Info?.Print(LogClass.Gpu,
                $"Xbox graphics stack initialized. ICD: {DxvkBootstrap.IsInitialized}, Loader: {XboxVulkanLoader.IsActive}");
        }

        public void FlushCaches()
        {
            PipelineCacheManager?.FlushToDisk();
            Logger.Info?.Print(LogClass.Gpu, "Xbox graphics caches flushed.");
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            PipelineCacheManager?.Dispose();
            DxvkBootstrap.Shutdown();
        }
    }
}
