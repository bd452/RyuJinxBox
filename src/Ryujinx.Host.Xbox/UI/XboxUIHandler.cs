using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using System;

namespace Ryujinx.Host.Xbox.UI
{
    /// <summary>
    /// Headless UI handler for Xbox. Since there's no desktop GUI on Xbox,
    /// applet dialogs are handled with sensible defaults.
    /// </summary>
    public class XboxUIHandler : IHostUIHandler
    {
        public IHostUITheme HostUITheme { get; } = new XboxUITheme();

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            Logger.Info?.Print(LogClass.Application,
                $"Software keyboard requested (guide text: '{args.GuideText}'). Returning empty string.");
            userText = args.InitialText ?? "";
            return true;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            Logger.Info?.Print(LogClass.Application, $"Message dialog: [{title}] {message}");
            return true;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            Logger.Info?.Print(LogClass.Application, $"Controller applet: PlayerCount={args.PlayerCountMin}-{args.PlayerCountMax}");
            return true;
        }

        public bool DisplayCabinetDialog(out string userText)
        {
            userText = "Amiibo";
            return true;
        }

        public void DisplayCabinetMessageDialog()
        {
            Logger.Info?.Print(LogClass.Application, "Cabinet message: Scan Amiibo now.");
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            Logger.Info?.Print(LogClass.Application, $"ExecuteProgram: kind={kind}, value={value}");
            // On Xbox, program transitions would restart the emulation context
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null)
        {
            Logger.Error?.Print(LogClass.Application, $"Error applet: [{title}] {message}");
            return false;
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new XboxDynamicTextInputHandler();
        }

        public UserProfile ShowPlayerSelectDialog()
        {
            Logger.Info?.Print(LogClass.Application, "Player select dialog requested. Returning null (use default).");
            return null;
        }

        public void TakeScreenshot()
        {
            Logger.Info?.Print(LogClass.Application, "Screenshot requested (not implemented on Xbox).");
        }
    }

    internal class XboxUITheme : IHostUITheme
    {
        public string FontFamily => "Segoe UI";
        public ThemeColor DefaultBackgroundColor => new(1f, 0.12f, 0.12f, 0.12f);
        public ThemeColor DefaultForegroundColor => new(1f, 1f, 1f, 1f);
        public ThemeColor DefaultBorderColor => new(1f, 0.24f, 0.24f, 0.24f);
        public ThemeColor SelectionBackgroundColor => new(1f, 0f, 0.47f, 0.84f);
        public ThemeColor SelectionForegroundColor => new(1f, 1f, 1f, 1f);
    }

    internal class XboxDynamicTextInputHandler : IDynamicTextInputHandler
    {
#pragma warning disable CS0067 // Events required by interface but unused in headless mode
        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent;
        public event KeyReleasedHandler KeyReleasedEvent;
#pragma warning restore CS0067

        public bool TextProcessingEnabled { get; set; }

        public void SetText(string text, int cursorBegin)
        {
            TextChangedEvent?.Invoke(text, cursorBegin, cursorBegin, false);
        }

        public void SetText(string text, int cursorBegin, int cursorEnd)
        {
            TextChangedEvent?.Invoke(text, cursorBegin, cursorEnd, false);
        }

        public void Dispose() { }
    }
}
