using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseDeck.App.ViewModels;

namespace MouseDeck.App.Views.Tabs;

public partial class AutomationTabView : UserControl
{
    private string? _previousHotkeyText;

    public AutomationTabView()
    {
        InitializeComponent();
    }

    private void OnRunningProcessPicked(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is string exe && DataContext is MainFlyoutViewModel vm)
        {
            vm.NewRuleProcessName = exe;
        }
    }

    private void OnHotkeyBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _previousHotkeyText = tb.Text;
            tb.Text = "Press keys...";
        }
    }

    private void OnHotkeyBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Text == "Press keys..." && _previousHotkeyText is not null)
        {
            tb.Text = _previousHotkeyText;
        }
    }

    private async void OnHotkeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string action || DataContext is not MainFlyoutViewModel vm)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            if (_previousHotkeyText is not null) tb.Text = _previousHotkeyText;
            Keyboard.ClearFocus();
            return;
        }

        // Ignore pure modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        uint win32Mods = 0;
        var parts = new List<string>();

        if (mods.HasFlag(ModifierKeys.Control)) { win32Mods |= 0x0002; parts.Add("Ctrl"); }
        if (mods.HasFlag(ModifierKeys.Alt)) { win32Mods |= 0x0001; parts.Add("Alt"); }
        if (mods.HasFlag(ModifierKeys.Shift)) { win32Mods |= 0x0004; parts.Add("Shift"); }
        if (mods.HasFlag(ModifierKeys.Windows)) { win32Mods |= 0x0008; parts.Add("Win"); }

        // Require at least one modifier or F-key so normal typing isn't stolen
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (win32Mods == 0 && !isFunctionKey)
            return;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        parts.Add(key.ToString());
        string display = string.Join("+", parts);

        _previousHotkeyText = display;
        tb.Text = display;
        Keyboard.ClearFocus();

        await vm.RebindHotkeyAsync(action, win32Mods, vk, display);
    }
}
