using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox.Input
{
    /// <summary>
    /// Xbox gamepad driver using the GameInput API.
    /// GameInput is the modern cross-platform input API for Xbox and Windows.
    /// Falls back to polling-based discovery when device callbacks aren't available.
    /// </summary>
    public unsafe class XboxGamepadDriver : Ryujinx.Input.IGamepadDriver
    {
        private const int MaxControllers = 8;

        private readonly Dictionary<string, int> _connectedGamepads;
        private readonly object _lock = new();
        private readonly System.Threading.Timer _pollingTimer;
        private volatile bool _isDisposed;

        private nint _pGameInput;

        public string DriverName => "Xbox";

        public ReadOnlySpan<string> GamepadsIds
        {
            get
            {
                lock (_lock)
                {
                    string[] ids = new string[_connectedGamepads.Count];
                    _connectedGamepads.Keys.CopyTo(ids, 0);
                    return ids;
                }
            }
        }

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public XboxGamepadDriver()
        {
            _connectedGamepads = new Dictionary<string, int>();

            if (OperatingSystem.IsWindows())
            {
                int hr = GameInputNative.GameInputCreate(out _pGameInput);
                if (hr < 0)
                {
                    Logger.Warning?.Print(LogClass.Hid,
                        $"GameInputCreate failed: 0x{hr:X8}. Controller input will not be available.");
                    _pGameInput = nint.Zero;
                }
                else
                {
                    Logger.Info?.Print(LogClass.Hid, "GameInput initialized successfully.");
                }
            }

            // Poll for controllers every 500ms
            _pollingTimer = new System.Threading.Timer(PollControllers, null, 0, 500);
        }

        private void PollControllers(object state)
        {
            if (_isDisposed || _pGameInput == nint.Zero) return;

            try
            {
                // Try to get a current gamepad reading.
                // If there's no gamepad connected, this will fail.
                int hr = GameInputNative.GetCurrentReading(
                    _pGameInput,
                    GameInputNative.GameInputKind.Gamepad,
                    nint.Zero,  // Any device
                    out nint pReading);

                if (hr >= 0 && pReading != nint.Zero)
                {
                    // Get the device from the reading
                    hr = GameInputNative.ReadingGetDevice(pReading, out nint pDevice);
                    if (hr >= 0 && pDevice != nint.Zero)
                    {
                        // Use the device pointer as a unique ID
                        string deviceId = $"xbox-{pDevice:X}";

                        lock (_lock)
                        {
                            if (!_connectedGamepads.ContainsKey(deviceId))
                            {
                                int index = _connectedGamepads.Count;
                                _connectedGamepads[deviceId] = index;
                                Logger.Info?.Print(LogClass.Hid, $"Xbox controller detected: {deviceId}");
                                OnGamepadConnected?.Invoke(deviceId);
                            }
                        }

                        GameInputNative.ComRelease(pDevice);
                    }

                    GameInputNative.ComRelease(pReading);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Hid, $"Error polling GameInput: {ex.Message}");
            }
        }

        public Ryujinx.Input.IGamepad GetGamepad(string id)
        {
            if (_pGameInput == nint.Zero) return null;

            lock (_lock)
            {
                if (_connectedGamepads.TryGetValue(id, out int index))
                {
                    return new XboxGamepad(_pGameInput, id, index);
                }
            }

            return null;
        }

        public IEnumerable<Ryujinx.Input.IGamepad> GetGamepads()
        {
            lock (_lock)
            {
                foreach (var kvp in _connectedGamepads)
                {
                    var gamepad = GetGamepad(kvp.Key);
                    if (gamepad != null)
                        yield return gamepad;
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;
                _pollingTimer.Dispose();

                lock (_lock)
                {
                    foreach (string id in _connectedGamepads.Keys)
                        OnGamepadDisconnected?.Invoke(id);

                    _connectedGamepads.Clear();
                }

                if (_pGameInput != nint.Zero)
                {
                    GameInputNative.ComRelease(_pGameInput);
                    _pGameInput = nint.Zero;
                }

                Logger.Info?.Print(LogClass.Hid, "Xbox gamepad driver disposed.");
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}
