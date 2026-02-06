using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Host.Xbox.Input
{
    /// <summary>
    /// Xbox gamepad driver using Windows.Gaming.Input.
    /// Maps Xbox controllers for use with Ryujinx's input system.
    /// </summary>
    public class XboxGamepadDriver : Ryujinx.Input.IGamepadDriver
    {
        private const int MaxControllers = 8;

        private readonly Dictionary<string, XboxGamepadInfo> _connectedGamepads;
        private readonly object _lock = new();
        private readonly System.Threading.Timer _pollingTimer;
        private volatile bool _isDisposed;

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
            _connectedGamepads = new Dictionary<string, XboxGamepadInfo>();

            // Poll for controller changes every 500ms
            _pollingTimer = new System.Threading.Timer(PollControllers, null, 0, 500);

            Logger.Info?.Print(LogClass.Hid, "Xbox gamepad driver initialized.");
        }

        private void PollControllers(object state)
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                // On actual Xbox hardware, this would use Windows.Gaming.Input.Gamepad.Gamepads
                // to enumerate connected controllers. For now, we maintain a polling-based approach
                // that can be wired up to the Xbox GDK input APIs.
                RefreshControllerState();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Hid, $"Error polling Xbox controllers: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes the connected controller state.
        /// On Xbox, this would query Windows.Gaming.Input.Gamepad.Gamepads.
        /// </summary>
        private void RefreshControllerState()
        {
            // Placeholder: On actual Xbox hardware, enumerate controllers from
            // Windows.Gaming.Input.Gamepad.Gamepads collection.
            // The actual implementation would compare the current set of gamepads
            // with the previously known set and fire connect/disconnect events.
        }

        /// <summary>
        /// Notifies the driver that a controller has been connected.
        /// Called by the Xbox platform layer when a gamepad is added.
        /// </summary>
        /// <param name="controllerId">The unique identifier for the controller.</param>
        /// <param name="controllerIndex">The controller index (0-7).</param>
        public void NotifyControllerConnected(string controllerId, int controllerIndex)
        {
            lock (_lock)
            {
                if (_connectedGamepads.ContainsKey(controllerId))
                {
                    return;
                }

                _connectedGamepads[controllerId] = new XboxGamepadInfo
                {
                    Id = controllerId,
                    Index = controllerIndex,
                    Name = $"Xbox Controller {controllerIndex + 1}",
                };

                Logger.Info?.Print(LogClass.Hid, $"Xbox controller connected: {controllerId} (index {controllerIndex})");
            }

            OnGamepadConnected?.Invoke(controllerId);
        }

        /// <summary>
        /// Notifies the driver that a controller has been disconnected.
        /// Called by the Xbox platform layer when a gamepad is removed.
        /// </summary>
        /// <param name="controllerId">The unique identifier for the controller.</param>
        public void NotifyControllerDisconnected(string controllerId)
        {
            lock (_lock)
            {
                if (!_connectedGamepads.Remove(controllerId))
                {
                    return;
                }

                Logger.Info?.Print(LogClass.Hid, $"Xbox controller disconnected: {controllerId}");
            }

            OnGamepadDisconnected?.Invoke(controllerId);
        }

        public Ryujinx.Input.IGamepad GetGamepad(string id)
        {
            lock (_lock)
            {
                if (_connectedGamepads.TryGetValue(id, out XboxGamepadInfo info))
                {
                    return new XboxGamepad(info);
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
                    yield return new XboxGamepad(kvp.Value);
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
                    {
                        OnGamepadDisconnected?.Invoke(id);
                    }

                    _connectedGamepads.Clear();
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

    /// <summary>
    /// Holds information about a connected Xbox gamepad.
    /// </summary>
    public class XboxGamepadInfo
    {
        public string Id { get; set; }
        public int Index { get; set; }
        public string Name { get; set; }
    }
}
