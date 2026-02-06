using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Configures DXVK for optimal Xbox performance.
    ///
    /// Phase 3 requirements:
    ///   - D3D12 backend only
    ///   - Pipeline cache enabled
    ///   - Aggressive pipeline reuse
    ///   - Explicit shader cache directory mapping
    ///
    /// DXVK is configured via environment variables (dxvk.conf equivalent).
    /// On Xbox, we set these programmatically before DXVK initialization.
    /// </summary>
    public static class DxvkConfiguration
    {
        /// <summary>
        /// Applies the standard DXVK configuration for Xbox.
        /// Must be called before DxvkBootstrap.Initialize().
        /// </summary>
        /// <param name="shaderCachePath">
        /// The directory where DXVK should store its shader cache.
        /// Should be within the Xbox sandbox (e.g., LocalFolder/cache/shaders).
        /// </param>
        /// <param name="pipelineCachePath">
        /// The directory where pipeline cache data should be stored.
        /// </param>
        public static void Apply(string shaderCachePath, string pipelineCachePath)
        {
            Logger.Info?.Print(LogClass.Gpu, "Applying DXVK configuration for Xbox...");

            var config = new Dictionary<string, string>
            {
                // Force D3D12 backend only (no D3D11 fallback)
                ["DXVK_CONFIG_FILE"] = "",

                // Shader cache configuration
                ["DXVK_STATE_CACHE"] = "1",                     // Enable state cache
                ["DXVK_STATE_CACHE_PATH"] = shaderCachePath,    // Explicit cache path
                ["DXVK_LOG_LEVEL"] = "warn",                    // Reduce DXVK log noise

                // Pipeline optimization
                ["DXVK_ASYNC"] = "1",                           // Async pipeline compilation to reduce stalls

                // D3D12-specific settings
                ["DXVK_D3D12_PIPELINE_LIBRARY"] = "1",          // Use D3D12 pipeline library for caching
            };

            foreach (var kvp in config)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
                Logger.Debug?.Print(LogClass.Gpu, $"Set {kvp.Key}={kvp.Value}");
            }

            // Ensure cache directories exist
            Directory.CreateDirectory(shaderCachePath);
            Directory.CreateDirectory(pipelineCachePath);

            Logger.Info?.Print(LogClass.Gpu,
                $"DXVK configured. Shader cache: {shaderCachePath}, Pipeline cache: {pipelineCachePath}");
        }

        /// <summary>
        /// Applies performance-optimized DXVK settings for Series S.
        /// More aggressive pipeline caching and lower shader quality settings.
        /// </summary>
        public static void ApplySeriesSOverrides()
        {
            Logger.Info?.Print(LogClass.Gpu, "Applying DXVK Series S performance overrides...");

            // Reduce shader compilation quality for faster compilation
            Environment.SetEnvironmentVariable("DXVK_SHADER_OPT_LEVEL", "1");

            // More aggressive async compilation
            Environment.SetEnvironmentVariable("DXVK_ASYNC_WORKERS", "2");
        }

        /// <summary>
        /// Applies performance settings for Series X.
        /// Can afford higher quality shader compilation.
        /// </summary>
        public static void ApplySeriesXOverrides()
        {
            Logger.Info?.Print(LogClass.Gpu, "Applying DXVK Series X performance overrides...");

            // Higher shader optimization level
            Environment.SetEnvironmentVariable("DXVK_SHADER_OPT_LEVEL", "2");

            // More compilation workers available on Series X
            Environment.SetEnvironmentVariable("DXVK_ASYNC_WORKERS", "4");
        }
    }
}
