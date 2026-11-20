using System.Runtime.InteropServices;
using MouseDeck.Core.Models;
using MouseDeck.Win32.Windowing;

namespace MouseDeck.Win32.Automation;

/// <summary>
/// Registers system-wide hotkeys using Win32 RegisterHotKey (0% CPU, no WH_KEYBOARD_LL hook,
/// fully compatible with competitive gaming anti-cheats).
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly MessageWindow _messageWindow;
    private readonly Dictionary<int, string> _registeredActions = [];
    private int _nextId = 100;
    private bool _disposed;

    public GlobalHotkeyService(MessageWindow messageWindow)
    {
        _messageWindow = messageWindow;
        _messageWindow.HotkeyPressed += OnHotkeyPressed;
    }

    public event Action<string>? HotkeyTriggered;

    public IReadOnlyList<string> ApplyBindings(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var errors = new List<string>();

        foreach (var binding in bindings)
        {
            if (!binding.Enabled || binding.VirtualKey == 0)
                continue;

            int id = _nextId++;
            bool ok = RegisterHotKey(_messageWindow.Handle, id, binding.Modifiers | MOD_NOREPEAT, binding.VirtualKey);
            if (ok)
            {
                _registeredActions[id] = binding.Action;
            }
            else
            {
                errors.Add($"Failed to register {binding.DisplayText} (may already be in use).");
            }
        }

        return errors;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registeredActions.Keys)
        {
            UnregisterHotKey(_messageWindow.Handle, id);
        }
        _registeredActions.Clear();
    }

    private void OnHotkeyPressed(int id)
    {
        if (_registeredActions.TryGetValue(id, out var action))
        {
            HotkeyTriggered?.Invoke(action);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messageWindow.HotkeyPressed -= OnHotkeyPressed;
        UnregisterAll();
    }
}
