using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Opt-in virtual PAW3950 8K mouse driver for UI verification, sleep/wake simulation,
/// and low-battery testing when no supported wireless mouse is plugged in.
/// </summary>
public sealed class VirtualMockProtocol : IMouseProtocol
{
    public const string VirtualDevicePath = "virtual://mousedeck/paw3950-demo";

    public static MouseDeviceInfo VirtualDevice { get; } = new()
    {
        DevicePath = VirtualDevicePath,
        VendorId = 0xFFFF,
        ProductId = 0x3950,
        VersionNumber = 0x0104,
        UsagePage = 0xFF02,
        Usage = 0x0002,
        InterfaceNumber = 1,
        InputReportLength = 64,
        OutputReportLength = 64,
        FeatureReportLength = 64,
        ProductName = "Virtual PAW3950 8K (Demo)",
        ManufacturerName = "MouseDeck Simulator",
        SerialNumber = "SIM-3950-001",
        CanOpenReadWrite = true,
        IsVirtual = true
    };

    private int _batteryPercent = 84;
    private bool _isCharging;
    private bool _isSleeping;
    private MouseProfile _storedDeviceProfile = new()
    {
        Id = "device-read",
        Name = "Device (Current)",
        IconKey = "Mouse",
        ActiveDpiStageIndex = 1,
        DpiStages =
        [
            new DpiStage { Dpi = 400, DpiY = 400, ColorHex = "#FFEF4444" },
            new DpiStage { Dpi = 1600, DpiY = 1600, ColorHex = "#FF38BDF8" },
            new DpiStage { Dpi = 3200, DpiY = 3200, ColorHex = "#FF22C55E" }
        ],
        PollingRateHz = 2000,
        LodMm = 1.0,
        DebounceMs = 2,
        MotionSync = true,
        SleepTimeoutMinutes = 5
    };

    public string ProtocolId => "virtual-paw3950-demo";
    public string DisplayName => "Virtual PAW3950 Simulator";
    public int Priority => 10;
    public MouseCapabilities Capabilities => MouseCapabilities.Paw3950HighRate;
    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
        [new DeviceMatchRule(0xFFFF, 0x3950, 0xFF02, FriendlyModelName: "Virtual PAW3950 8K (Demo)", TransportHint: MouseTransportKind.Virtual)];

    public int SimulatedBatteryPercent
    {
        get => _batteryPercent;
        set => _batteryPercent = Math.Clamp(value, 1, 100);
    }

    public bool SimulatedCharging
    {
        get => _isCharging;
        set
        {
            _isCharging = value;
            if (value) _isSleeping = false;
        }
    }

    public bool SimulatedSleeping
    {
        get => _isSleeping;
        set
        {
            _isSleeping = value;
            if (value) _isCharging = false;
        }
    }

    public int MatchScore(MouseDeviceInfo device) =>
        device.IsVirtual && device.DevicePath == VirtualDevicePath ? 200 : 0;

    public MouseTransportKind DetectTransport(MouseDeviceInfo device) =>
        _isCharging ? MouseTransportKind.WiredUsb : MouseTransportKind.Virtual;

    public Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        bool awake = !_isSleeping;
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Info, "Virtual", string.Empty,
            awake ? "Virtual Ping: Awake" : "Virtual Ping: Deep Sleep"));
        return Task.FromResult(awake);
    }

    public Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        if (_isSleeping)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "Virtual", "08 04 00 00 00 00 00 00 00 00",
                "Virtual Battery: Mouse Sleeping (no RF ack)"));
            return Task.FromResult<BatteryReading?>(new BatteryReading(0, false, null, IsMouseAwake: false));
        }

        int voltage = 3450 + (_batteryPercent * 7);
        var fakeRx = new byte[] { 0x08, 0x04, 0x00, 0x00, 0x00, 0x04, (byte)_batteryPercent, (byte)(_isCharging ? 1 : 0), (byte)(voltage >> 8), (byte)(voltage & 0xFF) };
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "Virtual", HidPacketHelper.ToHexString(fakeRx),
            $"Virtual Battery: {_batteryPercent}% ({voltage} mV){(_isCharging ? " Charging" : "")}"));

        return Task.FromResult<BatteryReading?>(new BatteryReading(_batteryPercent, _isCharging, voltage, IsMouseAwake: true));
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        if (_isSleeping) return Task.FromResult<MouseProfile?>(null);
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "Virtual",
            $"06 AA 03 08 {_storedDeviceProfile.PollingRateHz:X4} {_storedDeviceProfile.CurrentDpi:X4}",
            $"Virtual ReadConfig: {_storedDeviceProfile.CurrentDpi} DPI, {_storedDeviceProfile.PollingRateHz} Hz, LOD {_storedDeviceProfile.LodMm:0.0}mm"));
        return Task.FromResult<MouseProfile?>(_storedDeviceProfile.Clone());
    }

    public Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
    {
        if (_isSleeping)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Info, "Virtual", string.Empty,
                $"Virtual Write Deferred: mouse is sleeping, profile '{profile.Name}' queued"));
            return Task.FromResult(false);
        }

        _storedDeviceProfile = profile.Clone();
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "Virtual",
            $"06 AA 10 02 {(profile.PollingRateHz & 0xFF):X2} {(profile.PollingRateHz >> 8):X2}",
            $"Virtual Apply '{profile.Name}': {profile.CurrentDpi} DPI, {profile.PollingRateHz} Hz, LOD {profile.LodMm:0.0}mm, Debounce {profile.DebounceMs}ms, MS={profile.MotionSync}"));
        return Task.FromResult(true);
    }

    public sealed class VirtualHidSession : IHidSession
    {
        public MouseDeviceInfo Device => VirtualDevice;
        public bool IsOpen => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default) => Task.FromResult(true);
        public Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default) => Task.FromResult<byte[]?>(new byte[Math.Max(expectedLength, 17)]);
        public Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default) => Task.FromResult(true);
        public Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default) => Task.FromResult<byte[]?>(new byte[Math.Max(expectedLength, 17)]);
    }
}
