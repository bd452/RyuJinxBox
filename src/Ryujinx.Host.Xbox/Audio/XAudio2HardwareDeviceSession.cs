using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Host.Xbox.Audio
{
    /// <summary>
    /// XAudio2-based audio device session for Xbox.
    ///
    /// Uses a small buffer queue to maintain low latency:
    ///   - Switch games expect tight audio timing
    ///   - Avoids large ring buffers
    ///   - Relies on XAudio2 buffer completion callbacks
    /// </summary>
    public sealed class XAudio2HardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private const int MaxBufferCount = 4;

        private readonly XAudio2HardwareDeviceDriver _driver;
        private readonly ConcurrentQueue<AudioBuffer> _queuedBuffers;
        private readonly ConcurrentQueue<AudioBuffer> _releasedBuffers;

        private ulong _playedSampleCount;
        private float _volume;
        private bool _isActive;
        private bool _isDisposed;

        public XAudio2HardwareDeviceSession(
            XAudio2HardwareDeviceDriver driver,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
            : base(memoryManager, sampleFormat, sampleRate, channelCount)
        {
            _driver = driver;
            _queuedBuffers = new ConcurrentQueue<AudioBuffer>();
            _releasedBuffers = new ConcurrentQueue<AudioBuffer>();
            _volume = 1.0f;

            // On actual Xbox:
            // 1. Create IXAudio2SourceVoice with callback
            // 2. Configure low-latency buffer parameters
            // 3. Set format to match requested sample format/rate/channels
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            if (_isDisposed)
            {
                return;
            }

            _queuedBuffers.Enqueue(buffer);

            if (_isActive)
            {
                SubmitPendingBuffers();
            }
        }

        /// <summary>
        /// Submits queued buffers to XAudio2 source voice.
        /// On actual Xbox, this would call IXAudio2SourceVoice::SubmitSourceBuffer.
        /// </summary>
        private void SubmitPendingBuffers()
        {
            while (_queuedBuffers.TryDequeue(out AudioBuffer buffer))
            {
                // On actual Xbox:
                // XAUDIO2_BUFFER xaudioBuffer = new()
                // {
                //     AudioBytes = (uint)buffer.DataSize,
                //     pAudioData = bufferDataPointer,
                //     pContext = bufferContext
                // };
                // sourceVoice.SubmitSourceBuffer(ref xaudioBuffer);

                // For now, simulate immediate consumption
                ulong sampleCount = GetSampleCount(buffer);
                Interlocked.Add(ref _playedSampleCount, sampleCount);
                _releasedBuffers.Enqueue(buffer);
            }
        }

        public override void Start()
        {
            if (_isDisposed || _isActive)
            {
                return;
            }

            _isActive = true;

            // On actual Xbox: sourceVoice.Start()
            SubmitPendingBuffers();

            Logger.Debug?.Print(LogClass.Audio, "XAudio2 session started.");
        }

        public override void Stop()
        {
            if (_isDisposed || !_isActive)
            {
                return;
            }

            _isActive = false;

            // On actual Xbox: sourceVoice.Stop()

            Logger.Debug?.Print(LogClass.Audio, "XAudio2 session stopped.");
        }

        public override void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);

            // On actual Xbox: sourceVoice.SetVolume(_volume)
        }

        public override float GetVolume()
        {
            return _volume;
        }

        public override ulong GetPlayedSampleCount()
        {
            return _playedSampleCount;
        }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            // Check if the buffer has been released (completed playback)
            if (_releasedBuffers.TryPeek(out AudioBuffer releasedBuffer))
            {
                if (releasedBuffer.DataPointer == buffer.DataPointer)
                {
                    _releasedBuffers.TryDequeue(out _);
                    return true;
                }
            }

            return false;
        }

        public override void PrepareToClose()
        {
            Stop();
        }

        public override void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Stop();

            _driver.UnregisterSession(this);

            // On actual Xbox: sourceVoice.DestroyVoice()

            // Drain remaining buffers
            while (_queuedBuffers.TryDequeue(out _)) { }
            while (_releasedBuffers.TryDequeue(out _)) { }
        }
    }
}
