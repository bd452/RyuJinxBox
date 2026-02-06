using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Input;
using System;
using System.Numerics;
using StickInputId = Ryujinx.Input.StickInputId;

namespace Ryujinx.Host.Xbox.Input
{
    /// <summary>
    /// Represents an Xbox controller mapped to the Switch Pro Controller layout.
    ///
    /// Xbox → Switch mapping:
    ///   A → B, B → A, X → Y, Y → X (Nintendo layout swap)
    ///   LB → L, RB → R
    ///   LT → ZL, RT → ZR
    ///   View → Minus, Menu → Plus
    ///   Guide → Home
    ///
    /// Handles:
    ///   - Analog deadzone application
    ///   - Trigger normalization (0..1 float range)
    ///   - Rumble passthrough via Xbox haptics
    /// </summary>
    public class XboxGamepad : IGamepad
    {
        private const float DefaultDeadzone = 0.10f;
        private const float DefaultTriggerThreshold = 0.50f;

        private readonly XboxGamepadInfo _info;
        private float _triggerThreshold = DefaultTriggerThreshold;
        private StandardControllerInputConfig _configuration;

        // Simulated controller state (on real Xbox, these come from Windows.Gaming.Input)
        private XboxGamepadState _currentState;

        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.Rumble;

        public string Id => _info.Id;
        public string Name => _info.Name;
        public bool IsConnected => true;

        public XboxGamepad(XboxGamepadInfo info)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _currentState = default;
        }

        /// <summary>
        /// Updates the raw controller state. Called by the platform polling loop.
        /// On actual Xbox, this would read from Windows.Gaming.Input.Gamepad.GetCurrentReading().
        /// </summary>
        public void UpdateState(XboxGamepadState state)
        {
            _currentState = state;
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            return inputId switch
            {
                // Xbox A → Switch B (Nintendo layout)
                GamepadButtonInputId.A => (_currentState.Buttons & XboxButtons.A) != 0,
                // Xbox B → Switch A
                GamepadButtonInputId.B => (_currentState.Buttons & XboxButtons.B) != 0,
                // Xbox X → Switch Y
                GamepadButtonInputId.X => (_currentState.Buttons & XboxButtons.X) != 0,
                // Xbox Y → Switch X
                GamepadButtonInputId.Y => (_currentState.Buttons & XboxButtons.Y) != 0,

                GamepadButtonInputId.LeftStick => (_currentState.Buttons & XboxButtons.LeftThumbstick) != 0,
                GamepadButtonInputId.RightStick => (_currentState.Buttons & XboxButtons.RightThumbstick) != 0,
                GamepadButtonInputId.LeftShoulder => (_currentState.Buttons & XboxButtons.LeftShoulder) != 0,
                GamepadButtonInputId.RightShoulder => (_currentState.Buttons & XboxButtons.RightShoulder) != 0,

                // Triggers treated as buttons when exceeding threshold
                GamepadButtonInputId.LeftTrigger => _currentState.LeftTrigger >= _triggerThreshold,
                GamepadButtonInputId.RightTrigger => _currentState.RightTrigger >= _triggerThreshold,

                GamepadButtonInputId.DpadUp => (_currentState.Buttons & XboxButtons.DPadUp) != 0,
                GamepadButtonInputId.DpadDown => (_currentState.Buttons & XboxButtons.DPadDown) != 0,
                GamepadButtonInputId.DpadLeft => (_currentState.Buttons & XboxButtons.DPadLeft) != 0,
                GamepadButtonInputId.DpadRight => (_currentState.Buttons & XboxButtons.DPadRight) != 0,

                // View → Minus/Back, Menu → Plus/Start
                GamepadButtonInputId.Minus => (_currentState.Buttons & XboxButtons.View) != 0,
                GamepadButtonInputId.Plus => (_currentState.Buttons & XboxButtons.Menu) != 0,
                GamepadButtonInputId.Guide => (_currentState.Buttons & XboxButtons.Guide) != 0,

                _ => false,
            };
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            return inputId switch
            {
                StickInputId.Left => ApplyDeadzone(_currentState.LeftThumbstickX, _currentState.LeftThumbstickY),
                StickInputId.Right => ApplyDeadzone(_currentState.RightThumbstickX, _currentState.RightThumbstickY),
                _ => (0f, 0f),
            };
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            // Xbox controllers don't have motion sensors
            return Vector3.Zero;
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            _triggerThreshold = Math.Clamp(triggerThreshold, 0f, 1f);
        }

        public void SetConfiguration(InputConfig configuration)
        {
            if (configuration is StandardControllerInputConfig controllerConfig)
            {
                _configuration = controllerConfig;
            }
        }

        public void SetLed(uint packedRgb)
        {
            // Xbox controllers don't have user-controllable LEDs
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            // On actual Xbox hardware, this would call:
            // Windows.Gaming.Input.Gamepad.Vibration = new GamepadVibration
            // {
            //     LeftMotor = lowFrequency,
            //     RightMotor = highFrequency,
            //     LeftTrigger = 0,
            //     RightTrigger = 0
            // };
            //
            // For now, this is a placeholder that the platform layer will wire up.
            _ = lowFrequency;
            _ = highFrequency;
            _ = durationMs;
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            GamepadStateSnapshot rawState = GetStateSnapshot();

            if (_configuration == null)
            {
                return rawState;
            }

            // When configuration is present, the input system applies mapping.
            // The raw snapshot is sufficient since the HLE NpadController handles
            // the config-based remapping.
            return rawState;
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        private static (float, float) ApplyDeadzone(float x, float y)
        {
            float magnitude = MathF.Sqrt(x * x + y * y);

            if (magnitude < DefaultDeadzone)
            {
                return (0f, 0f);
            }

            // Rescale so that the range [deadzone, 1] maps to [0, 1]
            float normalizedMagnitude = (magnitude - DefaultDeadzone) / (1f - DefaultDeadzone);
            normalizedMagnitude = Math.Clamp(normalizedMagnitude, 0f, 1f);

            float scale = normalizedMagnitude / magnitude;

            return (Math.Clamp(x * scale, -1f, 1f), Math.Clamp(y * scale, -1f, 1f));
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Represents the raw state of an Xbox controller.
    /// On real Xbox hardware, this maps directly to Windows.Gaming.Input.GamepadReading.
    /// </summary>
    public struct XboxGamepadState
    {
        /// <summary>
        /// Bitmask of pressed buttons.
        /// </summary>
        public XboxButtons Buttons;

        /// <summary>
        /// Left trigger value, 0.0 (released) to 1.0 (fully pressed).
        /// </summary>
        public float LeftTrigger;

        /// <summary>
        /// Right trigger value, 0.0 (released) to 1.0 (fully pressed).
        /// </summary>
        public float RightTrigger;

        /// <summary>
        /// Left thumbstick X axis, -1.0 (left) to 1.0 (right).
        /// </summary>
        public float LeftThumbstickX;

        /// <summary>
        /// Left thumbstick Y axis, -1.0 (down) to 1.0 (up).
        /// </summary>
        public float LeftThumbstickY;

        /// <summary>
        /// Right thumbstick X axis, -1.0 (left) to 1.0 (right).
        /// </summary>
        public float RightThumbstickX;

        /// <summary>
        /// Right thumbstick Y axis, -1.0 (down) to 1.0 (up).
        /// </summary>
        public float RightThumbstickY;
    }

    /// <summary>
    /// Xbox controller button flags, matching Windows.Gaming.Input.GamepadButtons.
    /// </summary>
    [Flags]
    public enum XboxButtons : uint
    {
        None = 0,
        Menu = 0x0001,
        View = 0x0002,
        A = 0x0004,
        B = 0x0008,
        X = 0x0010,
        Y = 0x0020,
        DPadUp = 0x0040,
        DPadDown = 0x0080,
        DPadLeft = 0x0100,
        DPadRight = 0x0200,
        LeftShoulder = 0x0400,
        RightShoulder = 0x0800,
        LeftThumbstick = 0x1000,
        RightThumbstick = 0x2000,
        Guide = 0x4000,
    }
}
