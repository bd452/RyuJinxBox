using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ryujinx.Graphics.Xbox
{
    /// <summary>
    /// Manages the pipeline cache for the Xbox graphics stack.
    ///
    /// Phase 3 requirements:
    ///   - Pipeline cache enabled with aggressive reuse
    ///   - Explicit shader cache directory mapping
    ///   - Reduce pipeline permutations
    ///   - Trim unused specialization constants
    ///   - Cap cache growth with LRU eviction
    ///
    /// This is critical for avoiding shader compile stalls,
    /// which would cause catastrophic stuttering during gameplay.
    /// </summary>
    public sealed class XboxPipelineCacheManager : IDisposable
    {
        private const string PipelineCacheFileName = "pipeline_cache.bin";
        private const string ShaderCacheDirectoryName = "shaders";

        private readonly string _pipelineCachePath;
        private readonly string _shaderCachePath;
        private readonly int _maxEntries;
        private readonly long _maxCacheSize;
        private readonly LinkedList<PipelineCacheEntry> _lruList;
        private readonly Dictionary<ulong, LinkedListNode<PipelineCacheEntry>> _cacheMap;
        private readonly Lock _lock = new();
        private long _currentCacheSize;
        private bool _isDirty;
        private bool _isDisposed;

        /// <summary>
        /// Number of entries currently in the cache.
        /// </summary>
        public int EntryCount
        {
            get
            {
                lock (_lock)
                {
                    return _cacheMap.Count;
                }
            }
        }

        /// <summary>
        /// Current total size of cached data in bytes.
        /// </summary>
        public long CurrentCacheSize => Interlocked.Read(ref _currentCacheSize);

        /// <summary>
        /// Number of cache hits since initialization.
        /// </summary>
        public long CacheHits { get; private set; }

        /// <summary>
        /// Number of cache misses since initialization.
        /// </summary>
        public long CacheMisses { get; private set; }

        /// <summary>
        /// Creates a new pipeline cache manager.
        /// </summary>
        /// <param name="cacheDirectory">Root directory for cache storage.</param>
        /// <param name="maxEntries">Maximum number of pipeline cache entries before LRU eviction.</param>
        /// <param name="maxCacheSize">Maximum total cache size in bytes before LRU eviction.</param>
        public XboxPipelineCacheManager(string cacheDirectory, int maxEntries, long maxCacheSize)
        {
            _pipelineCachePath = Path.Combine(cacheDirectory, PipelineCacheFileName);
            _shaderCachePath = Path.Combine(cacheDirectory, ShaderCacheDirectoryName);
            _maxEntries = maxEntries;
            _maxCacheSize = maxCacheSize;
            _lruList = new LinkedList<PipelineCacheEntry>();
            _cacheMap = new Dictionary<ulong, LinkedListNode<PipelineCacheEntry>>();

            Directory.CreateDirectory(cacheDirectory);
            Directory.CreateDirectory(_shaderCachePath);

            Logger.Info?.Print(LogClass.Gpu,
                $"Pipeline cache manager initialized. Max entries: {maxEntries}, Max size: {maxCacheSize / (1024 * 1024)} MB");
        }

        /// <summary>
        /// Attempts to retrieve a cached pipeline entry.
        /// Promotes the entry to the front of the LRU list on hit.
        /// </summary>
        /// <param name="hash">The pipeline hash.</param>
        /// <param name="data">The cached pipeline data, if found.</param>
        /// <returns>True if the entry was found in cache.</returns>
        public bool TryGetCachedPipeline(ulong hash, out byte[] data)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(hash, out var node))
                {
                    // Move to front of LRU list (most recently used)
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);

                    data = node.Value.Data;
                    CacheHits++;
                    return true;
                }
            }

            data = null;
            CacheMisses++;
            return false;
        }

        /// <summary>
        /// Adds or updates a pipeline cache entry.
        /// Triggers LRU eviction if the cache exceeds limits.
        /// </summary>
        /// <param name="hash">The pipeline hash.</param>
        /// <param name="data">The compiled pipeline data.</param>
        public void CachePipeline(ulong hash, byte[] data)
        {
            lock (_lock)
            {
                // Update existing entry
                if (_cacheMap.TryGetValue(hash, out var existingNode))
                {
                    Interlocked.Add(ref _currentCacheSize, -existingNode.Value.Data.Length);
                    existingNode.Value = new PipelineCacheEntry(hash, data);
                    Interlocked.Add(ref _currentCacheSize, data.Length);

                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);

                    _isDirty = true;
                    return;
                }

                // Add new entry
                var entry = new PipelineCacheEntry(hash, data);
                var node = _lruList.AddFirst(entry);
                _cacheMap[hash] = node;
                Interlocked.Add(ref _currentCacheSize, data.Length);

                _isDirty = true;

                // Evict if necessary
                EvictIfNeeded();
            }
        }

        /// <summary>
        /// Evicts the least recently used entries until the cache is within limits.
        /// </summary>
        private void EvictIfNeeded()
        {
            while (_cacheMap.Count > _maxEntries ||
                   Interlocked.Read(ref _currentCacheSize) > _maxCacheSize)
            {
                if (_lruList.Last == null)
                {
                    break;
                }

                var lruNode = _lruList.Last;
                var lruEntry = lruNode.Value;

                _lruList.RemoveLast();
                _cacheMap.Remove(lruEntry.Hash);
                Interlocked.Add(ref _currentCacheSize, -lruEntry.Data.Length);

                Logger.Debug?.Print(LogClass.Gpu, $"Pipeline cache LRU eviction: hash=0x{lruEntry.Hash:X16}");
            }
        }

        /// <summary>
        /// Persists the pipeline cache to disk.
        /// Should be called periodically and during suspension/shutdown.
        /// </summary>
        public void FlushToDisk()
        {
            if (!_isDirty)
            {
                return;
            }

            try
            {
                lock (_lock)
                {
                    using var stream = new FileStream(_pipelineCachePath, FileMode.Create, FileAccess.Write);
                    using var writer = new BinaryWriter(stream);

                    // Write header
                    writer.Write((uint)1); // Version
                    writer.Write((uint)_cacheMap.Count);

                    // Write entries
                    foreach (var node in _lruList)
                    {
                        writer.Write(node.Hash);
                        writer.Write(node.Data.Length);
                        writer.Write(node.Data);
                    }

                    _isDirty = false;
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"Pipeline cache flushed to disk: {_cacheMap.Count} entries, " +
                    $"{Interlocked.Read(ref _currentCacheSize) / 1024} KB");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to flush pipeline cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the pipeline cache from disk, if available.
        /// Should be called during initialization before any rendering begins.
        /// </summary>
        public void LoadFromDisk()
        {
            if (!File.Exists(_pipelineCachePath))
            {
                Logger.Info?.Print(LogClass.Gpu, "No existing pipeline cache found on disk.");
                return;
            }

            try
            {
                using var stream = new FileStream(_pipelineCachePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                uint version = reader.ReadUInt32();

                if (version != 1)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Pipeline cache version mismatch (expected 1, got {version}). Discarding.");
                    return;
                }

                uint entryCount = reader.ReadUInt32();

                for (uint i = 0; i < entryCount; i++)
                {
                    ulong hash = reader.ReadUInt64();
                    int dataLength = reader.ReadInt32();
                    byte[] data = reader.ReadBytes(dataLength);

                    CachePipeline(hash, data);
                }

                _isDirty = false;

                Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache loaded from disk: {entryCount} entries.");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to load pipeline cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path where shader cache files should be stored.
        /// Used by the DXVK configuration to route shader caches.
        /// </summary>
        public string GetShaderCachePath()
        {
            return _shaderCachePath;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            FlushToDisk();

            lock (_lock)
            {
                _lruList.Clear();
                _cacheMap.Clear();
            }
        }
    }

    /// <summary>
    /// Represents a single entry in the pipeline cache.
    /// </summary>
    internal record struct PipelineCacheEntry(ulong Hash, byte[] Data);
}
