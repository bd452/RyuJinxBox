using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Host.Xbox.Native;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Host.Xbox.Audio
{
    /// <summary>
    /// XAudio2-based audio session using real COM interop.
    /// Manages an IXAudio2SourceVoice and feeds it audio buffers from the emulator.
    /// </summary>
    public sealed unsafe class XAudio2HardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly XAudio2HardwareDeviceDriver _driver;
        private readonly ConcurrentQueue<XAudio2AudioBuffer> _queuedBuffers;
        private readonly DynamicRingBuffer _ringBuffer;
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly int _bytesPerFrame;
        private readonly uint _sampleCount;

        /// <summary>
        /// Tracks pinned GCHandles for buffers submitted to XAudio2.
        /// XAudio2 reads from these buffers asynchronously, so they must stay pinned
        /// until playback completes. We free them when GetState shows they've been consumed.
        /// </summary>
        private readonly Queue<GCHandle> _pinnedBuffers;

        private nint _pSourceVoice;
        private ulong _playedSampleCount;
        private uint _totalBuffersSubmitted;
        private float _volume;
        private bool _started;
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
            _updateRequiredEvent = _driver.GetUpdateRequiredEvent();
            _queuedBuffers = new ConcurrentQueue<XAudio2AudioBuffer>();
            _ringBuffer = new DynamicRingBuffer();
            _pinnedBuffers = new Queue<GCHandle>();
            _volume = 1.0f;

            _bytesPerFrame = BackendHelper.GetSampleSize(RequestedSampleFormat) * (int)RequestedChannelCount;
            // Target ~10ms buffer at the given sample rate
            _sampleCount = Math.Max(480, sampleRate / 100);

            CreateSourceVoice();
        }

        private void CreateSourceVoice()
        {
            XAudio2Native.WAVEFORMATEX format = new()
            {
                nChannels = (ushort)RequestedChannelCount,
                nSamplesPerSec = RequestedSampleRate,
                wBitsPerSample = (ushort)(BackendHelper.GetSampleSize(RequestedSampleFormat) * 8),
            };

            format.nBlockAlign = (ushort)(format.nChannels * format.wBitsPerSample / 8);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
            format.cbSize = 0;

            format.wFormatTag = RequestedSampleFormat switch
            {
                SampleFormat.PcmFloat => XAudio2Native.WAVE_FORMAT_IEEE_FLOAT,
                _ => XAudio2Native.WAVE_FORMAT_PCM,
            };

            int hr = XAudio2Native.CreateSourceVoice(
                _driver.XAudio2Handle,
                out _pSourceVoice,
                &format,
                0,     // flags
                2.0f,  // maxFrequencyRatio
                nint.Zero);

            if (hr < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"CreateSourceVoice failed with HRESULT 0x{hr:X8}");
                _pSourceVoice = nint.Zero;
            }
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            if (_isDisposed || _pSourceVoice == nint.Zero) return;

            XAudio2AudioBuffer driverBuffer = new(buffer.DataPointer, GetSampleCount(buffer));
            _ringBuffer.Write(buffer.Data, 0, buffer.Data.Length);
            _queuedBuffers.Enqueue(driverBuffer);

            if (_started)
            {
                SubmitRingBufferData();
            }
        }

        private void SubmitRingBufferData()
        {
            if (_pSourceVoice == nint.Zero) return;

            // Free completed buffers first
            FreeCompletedBuffers();

            // Check how many buffers XAudio2 currently has queued
            XAudio2Native.SourceVoiceGetState(_pSourceVoice, out var state, 0);

            // Keep at most 3 buffers queued to maintain low latency
            if (state.BuffersQueued >= 3) return;

            int bytesAvailable = _ringBuffer.Length;
            if (bytesAvailable == 0) return;

            int targetBytes = (int)(_sampleCount * _bytesPerFrame);
            int bytesToRead = Math.Min(bytesAvailable, targetBytes);

            // Align to frame boundary
            bytesToRead = bytesToRead / _bytesPerFrame * _bytesPerFrame;
            if (bytesToRead == 0) return;

            byte[] data = new byte[bytesToRead];
            _ringBuffer.Read(data, 0, bytesToRead);

            // Pin the buffer and submit to XAudio2
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);

            XAudio2Native.XAUDIO2_BUFFER xaBuffer = new()
            {
                AudioBytes = (uint)bytesToRead,
                pAudioData = (byte*)handle.AddrOfPinnedObject(),
                pContext = nint.Zero,
            };

            int hr = XAudio2Native.SourceVoiceSubmitSourceBuffer(_pSourceVoice, &xaBuffer);
            if (hr < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"SubmitSourceBuffer failed: 0x{hr:X8}");
                handle.Free();
                return;
            }

            // Track the pinned handle so we can free it when XAudio2 is done
            _pinnedBuffers.Enqueue(handle);
            _totalBuffersSubmitted++;

            // Track sample count for emulator buffer tracking
            ulong samplesSubmitted = GetSampleCount(bytesToRead);
            Interlocked.Add(ref _playedSampleCount, samplesSubmitted);

            // Check if any queued emulator buffers are consumed
            bool needUpdate = false;
            ulong availableSamples = samplesSubmitted;

            while (availableSamples > 0 && _queuedBuffers.TryPeek(out XAudio2AudioBuffer driverBuffer))
            {
                ulong remaining = driverBuffer.SampleCount - Interlocked.Read(ref driverBuffer.SamplePlayed);
                ulong played = Math.Min(remaining, availableSamples);
                ulong current = Interlocked.Add(ref driverBuffer.SamplePlayed, played);
                availableSamples -= played;

                if (current >= driverBuffer.SampleCount)
                {
                    _queuedBuffers.TryDequeue(out _);
                    needUpdate = true;
                }
            }

            if (needUpdate)
            {
                _updateRequiredEvent.Set();
            }
        }

        /// <summary>
        /// Frees GCHandles for buffers that XAudio2 has finished playing.
        /// Compares the number of buffers currently queued in XAudio2 against
        /// our total submitted count to determine how many have completed.
        /// </summary>
        private void FreeCompletedBuffers()
        {
            if (_pSourceVoice == nint.Zero || _pinnedBuffers.Count == 0) return;

            XAudio2Native.SourceVoiceGetState(_pSourceVoice, out var state, 0);

            // Number of buffers that have completed = total submitted - still queued
            int completedCount = (int)(_totalBuffersSubmitted - state.BuffersQueued);
            int toFree = Math.Min(completedCount, _pinnedBuffers.Count);

            // The completed count should always be >= pinnedBuffers that need freeing,
            // but clamp to be safe
            toFree = Math.Max(0, toFree);

            // Free the oldest handles (they complete in FIFO order)
            while (toFree > 0 && _pinnedBuffers.TryDequeue(out GCHandle handle))
            {
                if (handle.IsAllocated)
                    handle.Free();
                toFree--;
            }
        }

        public override void Start()
        {
            if (_isDisposed || _started || _pSourceVoice == nint.Zero) return;
            _started = true;

            XAudio2Native.SourceVoiceStart(_pSourceVoice);
            SubmitRingBufferData();
        }

        public override void Stop()
        {
            if (_isDisposed || !_started || _pSourceVoice == nint.Zero) return;
            _started = false;

            XAudio2Native.SourceVoiceStop(_pSourceVoice);
        }

        public override void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            if (_pSourceVoice != nint.Zero)
            {
                XAudio2Native.VoiceSetVolume(_pSourceVoice, _volume * _driver.Volume);
            }
        }

        public override float GetVolume() => _volume;

        public override ulong GetPlayedSampleCount()
        {
            if (_pSourceVoice != nint.Zero)
            {
                XAudio2Native.SourceVoiceGetState(_pSourceVoice, out var state, 0);
                return state.SamplesPlayed;
            }
            return Interlocked.Read(ref _playedSampleCount);
        }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            if (!_queuedBuffers.TryPeek(out XAudio2AudioBuffer driverBuffer))
                return true;

            return driverBuffer.DriverIdentifier != buffer.DataPointer;
        }

        public override void PrepareToClose()
        {
            Stop();
        }

        public override void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();

            if (_pSourceVoice != nint.Zero)
            {
                XAudio2Native.SourceVoiceFlushSourceBuffers(_pSourceVoice);
                XAudio2Native.VoiceDestroy(_pSourceVoice);
                _pSourceVoice = nint.Zero;
            }

            // Free all remaining pinned buffers
            while (_pinnedBuffers.TryDequeue(out GCHandle handle))
            {
                if (handle.IsAllocated)
                    handle.Free();
            }

            _driver.UnregisterSession(this);

            while (_queuedBuffers.TryDequeue(out _)) { }
        }
    }

    internal class XAudio2AudioBuffer
    {
        public readonly ulong DriverIdentifier;
        public readonly ulong SampleCount;
        public ulong SamplePlayed;

        public XAudio2AudioBuffer(ulong driverIdentifier, ulong sampleCount)
        {
            DriverIdentifier = driverIdentifier;
            SampleCount = sampleCount;
            SamplePlayed = 0;
        }
    }
}
