using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Native;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Host.Xbox.Audio
{
    /// <summary>
    /// XAudio2-based hardware audio device driver for Xbox.
    /// Uses real COM interop to XAudio2 via P/Invoke.
    /// </summary>
    public sealed unsafe class XAudio2HardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<XAudio2HardwareDeviceSession, byte> _sessions;
        private bool _isDisposed;

        private nint _pXAudio2;
        private nint _pMasteringVoice;

        public float Volume { get; set; }

        public XAudio2HardwareDeviceDriver()
        {
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _sessions = new ConcurrentDictionary<XAudio2HardwareDeviceSession, byte>();
            Volume = 1.0f;

            int hr = XAudio2Native.XAudio2Create(out _pXAudio2, 0, XAudio2Native.XAUDIO2_DEFAULT_PROCESSOR);
            if (hr < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"XAudio2Create failed with HRESULT 0x{hr:X8}");
                _pXAudio2 = nint.Zero;
                return;
            }

            hr = XAudio2Native.CreateMasteringVoice(_pXAudio2, out _pMasteringVoice, 2, 48000);
            if (hr < 0)
            {
                Logger.Error?.Print(LogClass.Audio, $"CreateMasteringVoice failed with HRESULT 0x{hr:X8}");
                XAudio2Native.ComRelease(_pXAudio2);
                _pXAudio2 = nint.Zero;
                _pMasteringVoice = nint.Zero;
                return;
            }

            // Start the audio engine
            XAudio2Native.ComCall(_pXAudio2, XAudio2Native.VTable_StartEngine);

            Logger.Info?.Print(LogClass.Audio, "XAudio2 initialized successfully.");
        }

        public static bool IsSupported
        {
            get
            {
                if (!OperatingSystem.IsWindows())
                    return false;

                // Try creating XAudio2 to verify support
                int hr = XAudio2Native.XAudio2Create(out nint pXAudio2, 0, XAudio2Native.XAUDIO2_DEFAULT_PROCESSOR);
                if (hr >= 0 && pXAudio2 != nint.Zero)
                {
                    XAudio2Native.ComRelease(pXAudio2);
                    return true;
                }
                return false;
            }
        }

        internal nint XAudio2Handle => _pXAudio2;

        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;
        public ManualResetEvent GetPauseEvent() => _pauseEvent;

        public IHardwareDeviceSession OpenDeviceSession(Direction direction, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
        {
            if (channelCount == 0) channelCount = 2;
            if (sampleRate == 0) sampleRate = 48000;

            if (direction != Direction.Output)
                throw new NotImplementedException("Input direction is not supported on XAudio2 backend.");

            if (_pXAudio2 == nint.Zero)
                throw new InvalidOperationException("XAudio2 is not initialized.");

            var session = new XAudio2HardwareDeviceSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        internal bool UnregisterSession(XAudio2HardwareDeviceSession session) =>
            _sessions.TryRemove(session, out _);

        public bool SupportsDirection(Direction direction) => direction == Direction.Output;
        public bool SupportsSampleRate(uint sampleRate) => sampleRate is > 0 and <= 200000;
        public bool SupportsSampleFormat(SampleFormat sampleFormat) => sampleFormat != SampleFormat.PcmInt24;
        public bool SupportsChannelCount(uint channelCount) => channelCount is 1 or 2 or 6;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var session in _sessions.Keys)
                session.Dispose();

            _sessions.Clear();

            if (_pMasteringVoice != nint.Zero)
            {
                XAudio2Native.VoiceDestroy(_pMasteringVoice);
                _pMasteringVoice = nint.Zero;
            }

            if (_pXAudio2 != nint.Zero)
            {
                XAudio2Native.ComRelease(_pXAudio2);
                _pXAudio2 = nint.Zero;
            }

            _updateRequiredEvent.Dispose();
            _pauseEvent.Dispose();

            Logger.Info?.Print(LogClass.Audio, "XAudio2 driver disposed.");
        }
    }
}
