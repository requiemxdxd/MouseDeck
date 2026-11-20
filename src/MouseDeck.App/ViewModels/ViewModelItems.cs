using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MouseDeck.Core.Models;

namespace MouseDeck.App.ViewModels;

public sealed partial class ProfileItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _shortName = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    public override string ToString() => Name;
}

public sealed partial class DpiStageItemViewModel : ObservableObject
{
    public int Index { get; init; }

    [ObservableProperty]
    private int _dpi;

    [ObservableProperty]
    private int _dpiY;

    [ObservableProperty]
    private string _colorHex = "#FFFFFFFF";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class PollingRateItemViewModel : ObservableObject
{
    public int RateHz { get; init; }
    public string Label => RateHz >= 1000 ? $"{RateHz / 1000}K" : RateHz.ToString();

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isSupported = true;
}

public sealed partial class BatterySegmentItemViewModel : ObservableObject
{
    public int Index { get; init; }

    [ObservableProperty]
    private Brush _fillBrush = Brushes.Transparent;

    [ObservableProperty]
    private bool _isFilled;
}

public sealed partial class MouseSwitchItemViewModel : ObservableObject
{
    public required string DevicePath { get; init; }
    public required string DeviceKey { get; init; }
    public required string DisplayName { get; init; }
    public required string ProtocolName { get; init; }
    public required string TransportBadge { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => $"{DisplayName} ({TransportBadge})";
}

public sealed partial class ProcessRuleItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;
}

public sealed partial class HidDeviceItemViewModel : ObservableObject
{
    public required MouseDeviceInfo Device { get; init; }
    public string MatchedProtocolName { get; init; } = "—";
    public bool HasMatchedProtocol { get; init; }

    [ObservableProperty]
    private bool _isPinned;

    public string AccessLabel => Device.IsVirtual
        ? "Demo"
        : Device.IsBluetooth
            ? "BT"
            : Device.CanOpenReadWrite
                ? "R/W"
                : "OS";

    public string Title => string.IsNullOrWhiteSpace(Device.ProductName)
        ? $"HID {Device.DeviceKey}"
        : Device.ProductName;

    public string Subtitle => Device.IsBluetooth
        ? $"{Device.DeviceKey} · Bluetooth · {(Device.OsBatteryPercent.HasValue ? $"{Device.OsBatteryPercent}%" : "GATT")}"
        : $"{Device.DeviceKey} · {Device.UsagePage:X4}:{Device.Usage:X4} · {Device.InterfaceLabel} · {Device.InputReportLength}/{Device.OutputReportLength}/{Device.FeatureReportLength}";
}

public sealed record TrayStyleChoice(TrayIconStyle Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record BackdropStyleChoice(FlyoutBackdropStyle Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed partial class ButtonMappingItemViewModel : ObservableObject
{
    public required string Key { get; init; }
    public required string ButtonLabel { get; init; }

    [ObservableProperty]
    private string _selectedAction = string.Empty;
}
