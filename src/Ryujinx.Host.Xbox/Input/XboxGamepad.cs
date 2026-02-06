using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.Host.Xbox.Native;
using Ryujinx.Input;
using System;
using System.Numerics;
using StickInputId = Ryujinx.Input.StickInputId;

namespace Ryujinx.Host.Xbox.Input
{
    /// <summary>
    /// Xbox controller implementation using the GameInput API.
    /// Reads actual hardware state via IGameInput::GetCurrentReading.
    ///
    /// Xbox → Switch Pro Controller mapping:
    ///   A → B, B → A, X → Y, Y → X (Nintendo layout)
    ///   LB → L, RB → R, LT → ZL, RT → ZR
    ///   View → Minus, Menu → Plus
    /// </summary>
    public unsafe class XboxGamepad : IGamepad
    {
        private const float DefaultDeadzone = 0.10f;
        private const float DefaultTriggerThreshold = 0.50f;

        private readonly nint _pGameInput;
        private readonly string _id;
        private readonly int _index;
        private float _triggerThreshold = DefaultTriggerThreshold;
        private StandardControllerInputConfig _configuration;

        // Cached state from last poll
        private GameInputNative.GameInputGamepadState _currentState;
        private nint _currentDevice;

        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.Rumble;
        public string Id => _id;
        public string Name => $"Xbox Controller {_index + 1}";
        public bool IsConnected => true;

        public XboxGamepad(nint pGameInput, string id, int index)
        {
            _pGameInput = pGameInput;
            _id = id;
            _index = index;
            PollState();
        }

        /// <summary>
        /// Reads the current state from hardware via GameInput.
        /// </summary>
        private void PollState()
        {
            if (_pGameInput == nint.Zero) return;

            int hr = GameInputNative.GetCurrentReading(
                _pGameInput,
                GameInputNative.GameInputKind.Gamepad,
                nint.Zero,
                out nint pReading);

            if (hr < 0 || pReading == nint.Zero) return;

            // Read gamepad state
            GameInputNative.ReadingGetGamepadState(pReading, out _currentState);

            // Get device handle for rumble
            if (_currentDevice == nint.Zero)
            {
                GameInputNative.ReadingGetDevice(pReading, out _currentDevice);
            }

            GameInputNative.ComRelease(pReading);
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            PollState();

            var buttons = _currentState.Buttons;

            return inputId switch
            {
                GamepadButtonInputId.A => (buttons & GameInputNative.GameInputGamepadButtons.A) != 0,
                GamepadButtonInputId.B => (buttons & GameInputNative.GameInputGamepadButtons.B) != 0,
                GamepadButtonInputId.X => (buttons & GameInputNative.GameInputGamepadButtons.X) != 0,
                GamepadButtonInputId.Y => (buttons & GameInputNative.GameInputGamepadButtons.Y) != 0,

                GamepadButtonInputId.LeftStick => (buttons & GameInputNative.GameInputGamepadButtons.LeftThumbstick) != 0,
                GamepadButtonInputId.RightStick => (buttons & GameInputNative.GameInputGamepadButtons.RightThumbstick) != 0,
                GamepadButtonInputId.LeftShoulder => (buttons & GameInputNative.GameInputGamepadButtons.LeftShoulder) != 0,
                GamepadButtonInputId.RightShoulder => (buttons & GameInputNative.GameInputGamepadButtons.RightShoulder) != 0,

                GamepadButtonInputId.LeftTrigger => _currentState.LeftTrigger >= _triggerThreshold,
                GamepadButtonInputId.RightTrigger => _currentState.RightTrigger >= _triggerThreshold,

                GamepadButtonInputId.DpadUp => (buttons & GameInputNative.GameInputGamepadButtons.DPadUp) != 0,
                GamepadButtonInputId.DpadDown => (buttons & GameInputNative.GameInputGamepadButtons.DPadDown) != 0,
                GamepadButtonInputId.DpadLeft => (buttons & GameInputNative.GameInputGamepadButtons.DPadLeft) != 0,
                GamepadButtonInputId.DpadRight => (buttons & GameInputNative.GameInputGamepadButtons.DPadRight) != 0,

                GamepadButtonInputId.Minus => (buttons & GameInputNative.GameInputGamepadButtons.View) != 0,
                GamepadButtonInputId.Plus => (buttons & GameInputNative.GameInputGamepadButtons.Menu) != 0,

                _ => false,
            };
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            PollState();

            return inputId switch
            {
                StickInputId.Left => ApplyDeadzone(_currentState.LeftThumbstickX, _currentState.LeftThumbstickY),
                StickInputId.Right => ApplyDeadzone(_currentState.RightThumbstickX, _currentState.RightThumbstickY),
                _ => (0f, 0f),
            };
        }

        public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;

        public void SetTriggerThreshold(float triggerThreshold)
        {
            _triggerThreshold = Math.Clamp(triggerThreshold, 0f, 1f);
        }

        public void SetConfiguration(InputConfig configuration)
        {
            if (configuration is StandardControllerInputConfig controllerConfig)
                _configuration = controllerConfig;
        }

        public void SetLed(uint packedRgb) { }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            if (_currentDevice == nint.Zero) return;

            GameInputNative.GameInputRumbleParams rumble = new()
            {
                LowFrequency = Math.Clamp(lowFrequency, 0f, 1f),
                HighFrequency = Math.Clamp(highFrequency, 0f, 1f),
                LeftTrigger = 0f,
                RightTrigger = 0f,
            };

            GameInputNative.DeviceSetRumbleState(_currentDevice, &rumble);
        }

        public GamepadStateSnapshot GetMappedStateSnapshot() => GetStateSnapshot();
        public GamepadStateSnapshot GetStateSnapshot() => IGamepad.GetStateSnapshot(this);

        private static (float, float) ApplyDeadzone(float x, float y)
        {
            float magnitude = MathF.Sqrt(x * x + y * y);

            if (magnitude < DefaultDeadzone)
                return (0f, 0f);

            float normalizedMagnitude = (magnitude - DefaultDeadzone) / (1f - DefaultDeadzone);
            normalizedMagnitude = Math.Clamp(normalizedMagnitude, 0f, 1f);
            float scale = normalizedMagnitude / magnitude;

            return (Math.Clamp(x * scale, -1f, 1f), Math.Clamp(y * scale, -1f, 1f));
        }

        public void Dispose()
        {
            // Stop rumble on dispose
            if (_currentDevice != nint.Zero)
            {
                GameInputNative.GameInputRumbleParams rumble = default;
                GameInputNative.DeviceSetRumbleState(_currentDevice, &rumble);
                GameInputNative.ComRelease(_currentDevice);
                _currentDevice = nint.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}
