using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Host.Xbox.Native;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
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

        private nint _pSourceVoice;
        private ulong _playedSampleCount;
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
            else
            {
                Logger.Debug?.Print(LogClass.Audio,
                    $"XAudio2 source voice created: {RequestedChannelCount}ch, {RequestedSampleRate}Hz, {RequestedSampleFormat}");
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
                pContext = GCHandle.ToIntPtr(handle),
            };

            int hr = XAudio2Native.SourceVoiceSubmitSourceBuffer(_pSourceVoice, &xaBuffer);
            if (hr < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"SubmitSourceBuffer failed: 0x{hr:X8}");
                handle.Free();
                return;
            }

            // Track sample count
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

            _driver.UnregisterSession(this);

            while (_queuedBuffers.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Tracks an audio buffer submitted to XAudio2.
    /// </summary>
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
