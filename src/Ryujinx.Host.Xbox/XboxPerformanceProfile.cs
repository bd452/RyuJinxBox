namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Identifies the Xbox hardware variant.
    /// </summary>
    public enum XboxHardwareModel
    {
        /// <summary>
        /// Unknown or undetected hardware.
        /// </summary>
        Unknown,

        /// <summary>
        /// Xbox Series S - GPU-limited, tighter memory.
        /// Prefer 720p-900p resolution.
        /// </summary>
        SeriesS,

        /// <summary>
        /// Xbox Series X - CPU-limited, more memory slack.
        /// 1080p feasible.
        /// </summary>
        SeriesX,
    }

    /// <summary>
    /// Performance tuning profile for Xbox Series S / Series X.
    ///
    /// Series S vs Series X considerations:
    ///   GPU:        Series S is the main bottleneck; Series X CPU is more visible
    ///   Memory:     Series S is tighter; Series X has more slack
    ///   Resolution: Series S prefers 720p-900p; Series X 1080p feasible
    /// </summary>
    public sealed class XboxPerformanceProfile
    {
        /// <summary>
        /// The detected hardware model.
        /// </summary>
        public XboxHardwareModel HardwareModel { get; }

        /// <summary>
        /// Target render resolution width.
        /// </summary>
        public int TargetResolutionWidth { get; }

        /// <summary>
        /// Target render resolution height.
        /// </summary>
        public int TargetResolutionHeight { get; }

        /// <summary>
        /// Maximum JIT code cache size in bytes.
        /// </summary>
        public long MaxCodeCacheSize { get; }

        /// <summary>
        /// Maximum shader cache entries before LRU eviction kicks in.
        /// </summary>
        public int MaxShaderCacheEntries { get; }

        /// <summary>
        /// Maximum pipeline cache size in bytes before LRU eviction.
        /// </summary>
        public long MaxPipelineCacheSize { get; }

        /// <summary>
        /// Maximum texture residency budget in bytes.
        /// </summary>
        public long MaxTextureResidency { get; }

        /// <summary>
        /// Number of shader compilation worker threads.
        /// </summary>
        public int ShaderCompilationThreads { get; }

        /// <summary>
        /// Thread priority for CPU emulation threads (0 = normal, higher = higher priority).
        /// </summary>
        public int CpuEmulationThreadPriority { get; }

        /// <summary>
        /// Thread priority for GPU submission threads.
        /// </summary>
        public int GpuSubmissionThreadPriority { get; }

        private XboxPerformanceProfile(
            XboxHardwareModel model,
            int targetWidth,
            int targetHeight,
            long maxCodeCache,
            int maxShaderEntries,
            long maxPipelineCache,
            long maxTextureResidency,
            int shaderThreads,
            int cpuPriority,
            int gpuPriority)
        {
            HardwareModel = model;
            TargetResolutionWidth = targetWidth;
            TargetResolutionHeight = targetHeight;
            MaxCodeCacheSize = maxCodeCache;
            MaxShaderCacheEntries = maxShaderEntries;
            MaxPipelineCacheSize = maxPipelineCache;
            MaxTextureResidency = maxTextureResidency;
            ShaderCompilationThreads = shaderThreads;
            CpuEmulationThreadPriority = cpuPriority;
            GpuSubmissionThreadPriority = gpuPriority;
        }

        /// <summary>
        /// Creates the tuning profile for Xbox Series S.
        /// Optimized for tighter memory and GPU constraints.
        /// </summary>
        public static XboxPerformanceProfile CreateSeriesS() => new(
            model: XboxHardwareModel.SeriesS,
            targetWidth: 1280,   // 720p
            targetHeight: 720,
            maxCodeCache: 128L * 1024 * 1024,         // 128 MB
            maxShaderEntries: 4096,
            maxPipelineCache: 256L * 1024 * 1024,     // 256 MB
            maxTextureResidency: 512L * 1024 * 1024,  // 512 MB
            shaderThreads: 2,
            cpuPriority: 2,
            gpuPriority: 1
        );

        /// <summary>
        /// Creates the tuning profile for Xbox Series X.
        /// More headroom for memory and GPU workloads.
        /// </summary>
        public static XboxPerformanceProfile CreateSeriesX() => new(
            model: XboxHardwareModel.SeriesX,
            targetWidth: 1920,   // 1080p
            targetHeight: 1080,
            maxCodeCache: 256L * 1024 * 1024,          // 256 MB
            maxShaderEntries: 8192,
            maxPipelineCache: 512L * 1024 * 1024,      // 512 MB
            maxTextureResidency: 1024L * 1024 * 1024,  // 1 GB
            shaderThreads: 4,
            cpuPriority: 2,
            gpuPriority: 1
        );

        /// <summary>
        /// Creates a profile for the specified hardware model.
        /// Falls back to Series S profile for unknown hardware (conservative).
        /// </summary>
        public static XboxPerformanceProfile Create(XboxHardwareModel model)
        {
            return model switch
            {
                XboxHardwareModel.SeriesX => CreateSeriesX(),
                XboxHardwareModel.SeriesS => CreateSeriesS(),
                _ => CreateSeriesS(), // Conservative default
            };
        }

        /// <summary>
        /// Detects the current Xbox hardware model.
        /// On actual Xbox, this would query system information via the GDK.
        /// </summary>
        public static XboxHardwareModel DetectHardware()
        {
            // On actual Xbox:
            // Use Windows.System.Profile.AnalyticsInfo or Xbox GDK APIs
            // to determine Series S vs Series X.
            //
            // Heuristic: Check total available memory or GPU capabilities.
            // Series X: ~13.5 GB usable game memory
            // Series S: ~8 GB usable game memory

            return XboxHardwareModel.Unknown;
        }
    }
}
