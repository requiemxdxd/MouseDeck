namespace MouseDeck.Core.Models;

public sealed record MouseDeviceInfo
{
    public required string DevicePath { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public int? InterfaceNumber { get; init; }
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }
    public string ProductName { get; init; } = "Unknown HID Device";
    public string ManufacturerName { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public bool CanOpenReadWrite { get; init; }
    public bool IsVirtual { get; init; }

    // Bluetooth / OS-provided telemetry fields (used when device is discovered via WinRT Bluetooth / PnP)
    public bool IsBluetooth { get; init; }
    public int? OsBatteryPercent { get; init; }
    public bool IsBluetoothConnected { get; init; }
    public bool HasHidBatteryUsage { get; init; }

    public string DeviceKey => IsBluetooth && VendorId == 0
        ? $"BT:{(string.IsNullOrWhiteSpace(SerialNumber) ? ProductName : SerialNumber)}"
        : $"{VendorId:X4}:{ProductId:X4}";

    public string UsageHex => IsBluetooth ? "BLE/BT" : $"0x{UsagePage:X4}:0x{Usage:X4}";
    public string InterfaceLabel => IsBluetooth ? "BT" : InterfaceNumber.HasValue ? $"MI_{InterfaceNumber.Value:00}" : "—";
    public bool IsVendorControlInterface => UsagePage >= 0xFF00;
    public bool IsStandardMouseCollection => UsagePage == 0x0001 && Usage == 0x0002;
    public bool IsHidBatteryCollection => UsagePage == 0x0084 || UsagePage == 0x0085 || (UsagePage == 0x0006 && Usage == 0x0020) || HasHidBatteryUsage;
    public string ReportLengthsSummary => IsBluetooth
        ? $"BT • {(OsBatteryPercent.HasValue ? $"{OsBatteryPercent}%" : "GATT")}"
        : $"In:{InputReportLength} Out:{OutputReportLength} Feat:{FeatureReportLength}";
}
