using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox.Native
{
    /// <summary>
    /// P/Invoke declarations for Windows.Gaming.Input (GameInput API).
    /// GameInput is the modern Xbox input API that works on both Xbox and desktop Windows.
    /// On Xbox GDK builds, GameInput is always available.
    /// On desktop Windows 10+, it's available via the GameInput redistributable.
    ///
    /// We use the C ABI (GameInput.h) rather than WinRT projections for broader compatibility.
    /// </summary>
    internal static unsafe class GameInputNative
    {
        private const string GameInputDll = "gameinput.dll";

        // HRESULT GameInputCreate(IGameInput** gameInput)
        [DllImport(GameInputDll)]
        internal static extern int GameInputCreate(out nint gameInput);

        // --- IGameInput VTable ---
        // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
        // IGameInput: 3=GetCurrentTimestamp, 4=GetCurrentReading, 5=GetNextReading,
        //             6=GetPreviousReading, 7=GetTemporalReading, 8=RegisterReadingCallback,
        //             9=RegisterDeviceCallback, 10=RegisterGuideButtonCallback,
        //             11=RegisterKeyboardLayoutCallback, 12=StopCallback,
        //             13=UnregisterCallback, 14=CreateDispatcher, 15=CreateAggregateDevice,
        //             16=FindDeviceFromId, 17=FindDeviceFromObject,
        //             18=FindDeviceFromPlatformHandle, 19=FindDeviceFromPlatformString,
        //             20=EnableOemDeviceSupport, 21=SetFocusPolicy
        internal const int VTable_Release = 2;
        internal const int VTable_GetCurrentReading = 4;
        internal const int VTable_RegisterDeviceCallback = 9;
        internal const int VTable_StopCallback = 12;

        // --- IGameInputReading VTable ---
        // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
        // IGameInputReading: 3=GetInputKind, 4=GetSequenceNumber, 5=GetTimestamp,
        //                    6=GetDevice, 7=GetRawReport, 8=GetControllerAxisCount,
        //                    9=GetControllerAxisState, 10=GetControllerButtonCount,
        //                    11=GetControllerButtonState, 12=GetControllerSwitchCount,
        //                    13=GetControllerSwitchState, 14=GetKeyCount, 15=GetKeyState,
        //                    16=GetMouseState, 17=GetTouchCount, 18=GetTouchState,
        //                    19=GetMotionState, 20=GetArcadeStickState, 21=GetFlightStickState,
        //                    22=GetGamepadState, 23=GetRacingWheelState, 24=GetUiNavigationState
        internal const int VTable_Reading_Release = 2;
        internal const int VTable_Reading_GetDevice = 6;
        internal const int VTable_Reading_GetGamepadState = 22;

        // --- IGameInputDevice VTable ---
        internal const int VTable_Device_Release = 2;
        internal const int VTable_Device_GetDeviceInfo = 3;
        internal const int VTable_Device_SetRumbleState = 25;

        // GameInputKind flags
        [Flags]
        internal enum GameInputKind : uint
        {
            Unknown = 0x00000000,
            RawDeviceReport = 0x00000001,
            ControllerAxis = 0x00000002,
            ControllerButton = 0x00000004,
            ControllerSwitch = 0x00000008,
            Controller = 0x0000000E,
            Keyboard = 0x00000010,
            Mouse = 0x00000020,
            Touch = 0x00000100,
            Motion = 0x00001000,
            ArcadeStick = 0x00010000,
            FlightStick = 0x00020000,
            Gamepad = 0x00040000,
            RacingWheel = 0x00080000,
            UiNavigation = 0x01000000,
        }

        // GameInputGamepadState (matches the C struct)
        [StructLayout(LayoutKind.Sequential)]
        internal struct GameInputGamepadState
        {
            public GameInputGamepadButtons Buttons;
            public float LeftTrigger;
            public float RightTrigger;
            public float LeftThumbstickX;
            public float LeftThumbstickY;
            public float RightThumbstickX;
            public float RightThumbstickY;
        }

        [Flags]
        internal enum GameInputGamepadButtons : uint
        {
            None = 0x00000000,
            Menu = 0x00000001,
            View = 0x00000002,
            A = 0x00000004,
            B = 0x00000008,
            X = 0x00000010,
            Y = 0x00000020,
            DPadUp = 0x00000040,
            DPadDown = 0x00000080,
            DPadLeft = 0x00000100,
            DPadRight = 0x00000200,
            LeftShoulder = 0x00000400,
            RightShoulder = 0x00000800,
            LeftThumbstick = 0x00001000,
            RightThumbstick = 0x00002000,
        }

        // GameInputRumbleParams
        [StructLayout(LayoutKind.Sequential)]
        internal struct GameInputRumbleParams
        {
            public float LowFrequency;
            public float HighFrequency;
            public float LeftTrigger;
            public float RightTrigger;
        }

        // GameInputDeviceInfo (partial - we only need a few fields)
        [StructLayout(LayoutKind.Sequential)]
        internal struct GameInputDeviceInfo
        {
            public uint InfoSize;
            public ushort VendorId;
            public ushort ProductId;
            public ushort RevisionNumber;
            public byte InterfaceNumber;
            public byte CollectionNumber;
            public GameInputKind SupportedInput;
            // ... more fields follow but we don't need them
        }

        // Helper to call COM Release
        internal static int ComRelease(nint pInterface)
        {
            if (pInterface == nint.Zero) return 0;
            nint vtable = *(nint*)pInterface;
            nint fnPtr = *((nint*)vtable + 2);
            var fn = (delegate* unmanaged[Stdcall]<nint, int>)fnPtr;
            return fn(pInterface);
        }

        /// <summary>
        /// Call IGameInput::GetCurrentReading to get the latest gamepad state.
        /// </summary>
        internal static int GetCurrentReading(nint pGameInput, GameInputKind inputKind, nint pDevice, out nint ppReading)
        {
            ppReading = nint.Zero;
            nint vtable = *(nint*)pGameInput;
            nint fnPtr = *((nint*)vtable + VTable_GetCurrentReading);
            fixed (nint* pp = &ppReading)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, GameInputKind, nint, nint*, int>)fnPtr;
                return fn(pGameInput, inputKind, pDevice, pp);
            }
        }

        /// <summary>
        /// Call IGameInputReading::GetGamepadState
        /// </summary>
        internal static bool ReadingGetGamepadState(nint pReading, out GameInputGamepadState state)
        {
            state = default;
            nint vtable = *(nint*)pReading;
            nint fnPtr = *((nint*)vtable + VTable_Reading_GetGamepadState);
            fixed (GameInputGamepadState* pState = &state)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, GameInputGamepadState*, byte>)fnPtr;
                return fn(pReading, pState) != 0;
            }
        }

        /// <summary>
        /// Call IGameInputReading::GetDevice
        /// </summary>
        internal static int ReadingGetDevice(nint pReading, out nint ppDevice)
        {
            ppDevice = nint.Zero;
            nint vtable = *(nint*)pReading;
            nint fnPtr = *((nint*)vtable + VTable_Reading_GetDevice);
            fixed (nint* pp = &ppDevice)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)fnPtr;
                return fn(pReading, pp);
            }
        }

        /// <summary>
        /// Call IGameInputDevice::SetRumbleState
        /// </summary>
        internal static void DeviceSetRumbleState(nint pDevice, GameInputRumbleParams* pParams)
        {
            nint vtable = *(nint*)pDevice;
            nint fnPtr = *((nint*)vtable + VTable_Device_SetRumbleState);
            var fn = (delegate* unmanaged[Stdcall]<nint, GameInputRumbleParams*, void>)fnPtr;
            fn(pDevice, pParams);
        }

        /// <summary>
        /// Call IGameInput::RegisterDeviceCallback
        /// </summary>
        internal static int RegisterDeviceCallback(
            nint pGameInput,
            nint pDevice,
            GameInputKind inputKind,
            uint statusFilter,
            uint enumerationKind,
            nint context,
            nint callbackFunc,
            out ulong callbackToken)
        {
            callbackToken = 0;
            nint vtable = *(nint*)pGameInput;
            nint fnPtr = *((nint*)vtable + VTable_RegisterDeviceCallback);
            fixed (ulong* pToken = &callbackToken)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, nint, GameInputKind, uint, uint, nint, nint, ulong*, int>)fnPtr;
                return fn(pGameInput, pDevice, inputKind, statusFilter, enumerationKind, context, callbackFunc, pToken);
            }
        }
    }
}
