using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Flow.Launcher.Core.Resource;
using Flow.Launcher.Helper;
using Flow.Launcher.Infrastructure.Hotkey;
using Flow.Launcher.Plugin;
using System.Threading;
using Flow.Launcher.Core.Plugin;
using Flow.Launcher.Infrastructure.Logger;

namespace Flow.Launcher
{
    public partial class HotkeyControl : UserControl
    {
        public HotkeyModel CurrentHotkey { get; private set; }
        public bool CurrentHotkeyAvailable { get; private set; }

        public event EventHandler HotkeyChanged;

        /// <summary>
        /// Designed for Preview Hotkey and KeyGesture.
        /// </summary>
        public bool ValidateKeyGesture { get; set; } = false;

        protected virtual void OnHotkeyChanged() => HotkeyChanged?.Invoke(this, EventArgs.Empty);

        private Func<int, int, SpecialKeyState, bool> callback { get; set; }

        public HotkeyControl()
        {
            InitializeComponent();

            tbMsgTextOriginal = tbMsg.Text;
            tbMsgForegroundColorOriginal = tbMsg.Foreground;

            callback = TbHotkey_OnPreviewKeyDown;

            GotFocus += (_, _) =>
            {
                PluginManager.API.RegisterGlobalKeyboardCallback(callback);
            };
            LostFocus += (_, _) =>
            {
                PluginManager.API.RemoveGlobalKeyboardCallback(callback);
                state.AltPressed = false;
                state.CtrlPressed = false;
                state.ShiftPressed = false;
                state.WinPressed = false;
            };
=======

        }

        private CancellationTokenSource hotkeyUpdateSource;

        private SpecialKeyState state = new();

        private bool TbHotkey_OnPreviewKeyDown(int keyevent, int vkcode, SpecialKeyState dummy)
        {
            var key = KeyInterop.KeyFromVirtualKey(vkcode);

            if ((KeyEvent)keyevent is not (KeyEvent.WM_KEYDOWN or KeyEvent.WM_SYSKEYDOWN))
            {
                switch (key)
                {
                    case Key.LeftAlt or Key.RightAlt:
                        state.AltPressed = false;
                        break;
                    case Key.LeftCtrl or Key.RightCtrl:
                        state.CtrlPressed = false;
                        break;
                    case Key.LeftShift or Key.RightShift:
                        state.ShiftPressed = false;
                        break;
                    case Key.LWin or Key.LWin:
                        state.WinPressed = false;
                        break;
                    default:
                        break;
                }
                return true;
            }

            switch (key)
            {
                case Key.LeftAlt or Key.RightAlt:
                    state.AltPressed = true;
                    break;
                case Key.LeftCtrl or Key.RightCtrl:
                    state.CtrlPressed = true;
                    break;
                case Key.LeftShift or Key.RightShift:
                    state.ShiftPressed = true;
                    break;
                case Key.LWin or Key.LWin:
                    state.WinPressed = true;
                    break;
            }


            hotkeyUpdateSource?.Cancel();
            hotkeyUpdateSource?.Dispose();
            hotkeyUpdateSource = new();
            var token = hotkeyUpdateSource.Token;


            var hotkeyModel = new HotkeyModel(
                state.AltPressed,
                state.ShiftPressed,
                state.WinPressed,
                state.CtrlPressed,
                key);

            if (hotkeyModel.Equals(CurrentHotkey))
            {
                return false;
            }
            Log.Debug("test hotkey" + hotkeyString);

            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                    await SetHotkeyAsync(hotkeyModel);
            });

            return false;
        }

        public async Task SetHotkeyAsync(HotkeyModel keyModel, bool triggerValidate = true)
        {
            tbHotkey.Text = keyModel.ToString();
            tbHotkey.Select(tbHotkey.Text.Length, 0);

            if (triggerValidate)
            {
                bool hotkeyAvailable = CheckHotkeyAvailability(keyModel, ValidateKeyGesture);
                CurrentHotkeyAvailable = hotkeyAvailable;
                SetMessage(hotkeyAvailable);
                OnHotkeyChanged();

                var token = hotkeyUpdateSource.Token;
                await Task.Delay(500, token);
                if (token.IsCancellationRequested)
                    return;

                if (CurrentHotkeyAvailable)
                {
                    CurrentHotkey = keyModel;
                    // To trigger LostFocus
                    FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
                    Keyboard.ClearFocus();
                }
            }
            else
            {
                CurrentHotkey = keyModel;
            }
        }
        
        public Task SetHotkeyAsync(string keyStr, bool triggerValidate = true)
        {
            return SetHotkeyAsync(new HotkeyModel(keyStr), triggerValidate);
        }

        private static bool CheckHotkeyAvailability(HotkeyModel hotkey, bool validateKeyGesture) => hotkey.Validate(validateKeyGesture) && HotKeyMapper.CheckAvailability(hotkey);

        public new bool IsFocused => tbHotkey.IsFocused;

        private void tbHotkey_LostFocus(object sender, RoutedEventArgs e)
        {
            tbHotkey.Text = CurrentHotkey?.ToString() ?? "";
            tbHotkey.Select(tbHotkey.Text.Length, 0);
        }

        private void tbHotkey_GotFocus(object sender, RoutedEventArgs e)
        {
            ResetMessage();
        }

        private void ResetMessage()
        {
            tbMsg.Text = InternationalizationManager.Instance.GetTranslation("flowlauncherPressHotkey");
            tbMsg.SetResourceReference(TextBox.ForegroundProperty, "Color05B");
        }

        private void SetMessage(bool hotkeyAvailable)
        {
            if (!hotkeyAvailable)
            {
                tbMsg.Foreground = new SolidColorBrush(Colors.Red);
                tbMsg.Text = InternationalizationManager.Instance.GetTranslation("hotkeyUnavailable");
            }
            else
            {
                tbMsg.Foreground = new SolidColorBrush(Colors.Green);
                tbMsg.Text = InternationalizationManager.Instance.GetTranslation("success");
            }
            tbMsg.Visibility = Visibility.Visible;
        }
    }
}
