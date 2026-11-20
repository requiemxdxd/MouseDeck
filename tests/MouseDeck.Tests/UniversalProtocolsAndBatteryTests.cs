using System.Text.Json;
using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Services;
using MouseDeck.Core.Transport;
using Xunit;

namespace MouseDeck.Tests;

public sealed class UniversalProtocolsAndBatteryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MouseDeckUniTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LogitechHidpp_BuildRequest_TagsDedicatedSwIdForZeroGHubConflict()
    {
        var req = LogitechHidPlusPlusProtocol.BuildRequest(LogitechHidPlusPlusProtocol.LongReportId, 0xFF, 0x05, 0x01, [0x10, 0x04]);
        Assert.Equal(20, req.Length);
        Assert.Equal(0x11, req[0]);
        Assert.Equal(0xFF, req[1]);
        Assert.Equal(0x05, req[2]);
        Assert.Equal((byte)((0x01 << 4) | LogitechHidPlusPlusProtocol.SoftwareId), req[3]);
        Assert.Equal(0x10, req[4]);
        Assert.Equal(0x04, req[5]);
    }

    [Fact]
    public void LogitechHidpp_ParsesUnifiedBattery_LegacyStatus_And_Voltage()
    {
        // Unified Battery 0x1004: 87%, recharging (status=1)
        var rx1004 = new byte[20];
        rx1004[4] = 87;
        rx1004[6] = 1;
        var b1004 = LogitechHidPlusPlusProtocol.ParseUnifiedBatteryResponse(rx1004);
        Assert.NotNull(b1004);
        Assert.Equal(87, b1004!.Percentage);
        Assert.True(b1004.IsCharging);
        Assert.True(b1004.IsMouseAwake);

        // Legacy 0x1000: 42%, discharging
        var rx1000 = new byte[7];
        rx1000[4] = 42;
        rx1000[6] = 0;
        var b1000 = LogitechHidPlusPlusProtocol.ParseLegacyBatteryStatusResponse(rx1000);
        Assert.NotNull(b1000);
        Assert.Equal(42, b1000!.Percentage);
        Assert.False(b1000.IsCharging);

        // Voltage 0x1001 (Li-Ion 3900 mV)
        var rx1001 = new byte[7];
        rx1001[4] = 0x0F;
        rx1001[5] = 0x3C; // 3900 mV
        rx1001[6] = 0x00;
        var bLi = LogitechHidPlusPlusProtocol.ParseBatteryVoltageResponse(rx1001);
        Assert.NotNull(bLi);
        Assert.Equal(3900, bLi!.VoltageMv);
        Assert.InRange(bLi.Percentage, 60, 75);

        // Voltage 0x1001 (AA Alkaline G305 1400 mV)
        var rxAa = new byte[7];
        rxAa[4] = 0x05;
        rxAa[5] = 0x78; // 1400 mV
        var bAa = LogitechHidPlusPlusProtocol.ParseBatteryVoltageResponse(rxAa);
        Assert.NotNull(bAa);
        Assert.Equal(1400, bAa!.VoltageMv);
        Assert.InRange(bAa.Percentage, 70, 80);
    }

    [Fact]
    public void RazerHid_BuildsValid90ByteFrameWithCrc_And_ParsesBattery()
    {
        var frame = RazerHidProtocol.BuildFeatureFrame(0x1F, 0x07, 0x80);
        Assert.Equal(91, frame.Length);
        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x1F, frame[2]);
        Assert.Equal(0x07, frame[7]);
        Assert.Equal(0x80, frame[8]);
        Assert.Equal(RazerHidProtocol.ComputeRazerCrc(frame.AsSpan(1, 90)), frame[89]);

        var rx = new byte[91];
        rx[1] = 0x02; // Status OK
        rx[10] = 191; // ~75% of 255
        Assert.Equal(75, RazerHidProtocol.ParseBatteryPercent(rx));

        rx[10] = 1;
        Assert.True(RazerHidProtocol.ParseChargingFlag(rx));
    }

    [Fact]
    public void MajorBrands_ParsesSteelSeries_Corsair_HyperX_AndRejectsHeadsets()
    {
        var proto = new MajorBrandsBatteryProtocol();

        // SteelSeries Aerox (1..20 scale -> *5%)
        var ss = MajorBrandsBatteryProtocol.ParseSteelSeriesResponse([0x00, 0xD2, 14, 0x00]);
        Assert.NotNull(ss);
        Assert.Equal(70, ss!.Percentage);
        Assert.False(ss.IsCharging);

        // Corsair Slipstream (825 tenths -> 83%)
        var cor = MajorBrandsBatteryProtocol.ParseCorsairResponse([0x00, 0x08, 0x02, 0x0F, 0x39, 0x03]);
        Assert.NotNull(cor);
        Assert.Equal(83, cor!.Percentage);

        // HyperX Pulsefire (54%, charging)
        var hx = MajorBrandsBatteryProtocol.ParseHyperXResponse([0x00, 0x51, 0x00, 0x00, 54, 1]);
        Assert.NotNull(hx);
        Assert.Equal(54, hx!.Percentage);
        Assert.True(hx.IsCharging);

        // Must NOT match SteelSeries Arctis headset or Corsair Virtuoso
        var headset = new MouseDeviceInfo
        {
            DevicePath = "hid://arctis",
            VendorId = MajorBrandsBatteryProtocol.SteelSeriesVid,
            ProductId = 0x12AD,
            UsagePage = 0xFF00,
            CanOpenReadWrite = true,
            ProductName = "SteelSeries Arctis Nova Pro Headset"
        };
        Assert.Equal(0, proto.MatchScore(headset));
    }

    [Fact]
    public void ProtocolRegistry_DeduplicatesSubCollectionsAndKeepsMultiplePhysicalMice()
    {
        var registry = new ProtocolRegistry([
            new CompXProtocol(),
            new LogitechHidPlusPlusProtocol(),
            new RazerHidProtocol(),
            new NordicHidProtocol(),
            new MajorBrandsBatteryProtocol(),
            new BluetoothMouseProtocol(),
            new GenericMouseProtocol()
        ]);

        var mad8kCtrl = new MouseDeviceInfo
        {
            DevicePath = "hid://mad8k_mi01",
            VendorId = 0x373B,
            ProductId = 0x1040,
            UsagePage = 0xFF02,
            Usage = 0x0002,
            InputReportLength = 17,
            OutputReportLength = 17,
            CanOpenReadWrite = true,
            ProductName = "MAD 8K DONGLE"
        };
        var mad8kPointer = new MouseDeviceInfo
        {
            DevicePath = "hid://mad8k_mi00",
            VendorId = 0x373B,
            ProductId = 0x1040,
            UsagePage = 0x0001,
            Usage = 0x0002,
            ProductName = "MAD 8K DONGLE"
        };
        var btMouse = new MouseDeviceInfo
        {
            DevicePath = "bt://mx_master_3s",
            ProductName = "MX Master 3S",
            SerialNumber = "AABBCC112233",
            IsBluetooth = true,
            IsBluetoothConnected = true,
            OsBatteryPercent = 64,
            CanOpenReadWrite = true
        };
        var wiredMouse = new MouseDeviceInfo
        {
            DevicePath = "hid://generic_wired",
            VendorId = 0x093A,
            ProductId = 0x2510,
            UsagePage = 0x0001,
            Usage = 0x0002,
            ProductName = "USB Optical Mouse"
        };

        var matched = registry.FindAllMatchedMice([mad8kPointer, mad8kCtrl, btMouse, wiredMouse]);

        // 3 unique physical mice (mad8kPointer deduplicated into mad8kCtrl!)
        Assert.Equal(3, matched.Count);
        Assert.Equal("compx-cx52850", matched[0].Protocol.ProtocolId);
        Assert.Contains(matched, m => m.Protocol.ProtocolId == "bluetooth-gatt-pnp" && m.Device.ProductName == "MX Master 3S");
        Assert.Contains(matched, m => m.Protocol.ProtocolId == "generic-hid-mouse" && m.Device.ProductName == "USB Optical Mouse");
    }

    [Fact]
    public async Task ReadMatchingInputReportAsync_SkipsAsyncUnrelatedPacketsFromVendorSoftware()
    {
        var session = new QueuedMockSession([
            [0x08, 0x99, 0x00, 0x00], // Unrelated async DPI event
            [0x02, 0x01, 0x00, 0x00], // Unrelated vendor packet
            [0x08, 0x04, 0x00, 0x00, 0x00, 0x00, 58, 0, 0x0E, 0xDA] // Matching CompX battery reply!
        ]);

        var matched = await ((IHidSession)session).ReadMatchingInputReportAsync(
            17,
            pkt => pkt.Length >= 2 && pkt[0] == 0x08 && pkt[1] == 0x04,
            timeoutMs: 250,
            maxAttempts: 4);

        Assert.NotNull(matched);
        Assert.Equal(58, matched![6]);
    }

    [Fact]
    public void BatteryHistoryService_RecordsPointsAndComputesRealDrainRate()
    {
        var cache = new CachedDeviceBattery { DeviceKey = "373B:1040" };
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);

        BatteryHistoryService.RecordPoint(cache, 90, false, t0);
        BatteryHistoryService.RecordPoint(cache, 86, false, t0.AddHours(1));
        BatteryHistoryService.RecordPoint(cache, 82, false, t0.AddHours(2));
        BatteryHistoryService.RecordPoint(cache, 78, false, t0.AddHours(3));

        Assert.Equal(4, cache.History.Count);
        double? drain = BatteryHistoryService.ComputeMeasuredDrainRatePerHour(cache.History);
        Assert.NotNull(drain);
        Assert.Equal(4.0, drain!.Value, precision: 2);

        // At 78% and 4%/hour -> 19.5 hours remaining
        double rem = BatteryHistoryService.EstimateRemainingHours(78, new MouseProfile(), drain);
        Assert.Equal(19.5, rem, precision: 1);
    }

    [Fact]
    public void BatteryHistoryService_IgnoresOvernightSleepGapWhenComputingActiveDrain()
    {
        var cache = new CachedDeviceBattery { DeviceKey = "373B:1040" };
        var t0 = DateTimeOffset.UtcNow.AddHours(-12);

        // Active 30 min -> sleeps 10 hours overnight at 70% -> active 1 hour dropping 70% -> 66%
        BatteryHistoryService.RecordPoint(cache, 70, false, t0);
        BatteryHistoryService.RecordPoint(cache, 70, false, t0.AddHours(10)); // overnight sleep gap (>25 min)
        BatteryHistoryService.RecordPoint(cache, 69, false, t0.AddHours(10).AddMinutes(15));
        BatteryHistoryService.RecordPoint(cache, 68, false, t0.AddHours(10).AddMinutes(30));
        BatteryHistoryService.RecordPoint(cache, 67, false, t0.AddHours(10).AddMinutes(45));
        BatteryHistoryService.RecordPoint(cache, 66, false, t0.AddHours(11));

        double? drain = BatteryHistoryService.ComputeMeasuredDrainRatePerHour(cache.History);
        Assert.NotNull(drain);
        // Active drain is 4%/hour (NOT diluted to 4%/11h = 0.36%/h by the 10-hour overnight sleep!)
        Assert.InRange(drain!.Value, 3.5, 4.5);
    }

    [Fact]
    public void BatteryHistoryService_ComputesCcCvChargingTimeToFull_AndCalibratesLive()
    {
        // 1. Default CC-CV prior at 100% -> 0 min
        var full = BatteryHistoryService.EstimateCharging(100, []);
        Assert.Equal(TimeSpan.Zero, full.RemainingTime);

        // 2. Default CC-CV prior at 80% takes ~30 min (slower CV phase!), at 30% takes ~65-70 min
        var prior80 = BatteryHistoryService.EstimateCharging(80, []);
        var prior30 = BatteryHistoryService.EstimateCharging(30, []);
        Assert.InRange(prior80.RemainingTime.TotalMinutes, 25, 35);
        Assert.InRange(prior30.RemainingTime.TotalMinutes, 60, 75);

        // 3. Live calibrated fast charger: +12% in 6 min during CC phase (2.0 CC-units/min)
        // Remaining from 42% -> 100% is (80-42) + 40 = 78 CC-units -> 78 / 2.0 = 39 min!
        var cache = new CachedDeviceBattery { DeviceKey = "373B:1040" };
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-6);
        BatteryHistoryService.RecordPoint(cache, 30, true, t0, 3850);
        BatteryHistoryService.RecordPoint(cache, 34, true, t0.AddMinutes(2), 3890);
        BatteryHistoryService.RecordPoint(cache, 38, true, t0.AddMinutes(4), 3930);
        BatteryHistoryService.RecordPoint(cache, 42, true, t0.AddMinutes(6), 3970);

        var calibrated = BatteryHistoryService.EstimateCharging(42, cache.History);
        Assert.True(calibrated.IsCalibrated);
        Assert.NotNull(calibrated.MeasuredPercentPerMinute);
        Assert.Equal(2.0, calibrated.MeasuredPercentPerMinute!.Value, precision: 1);
        Assert.InRange(calibrated.RemainingTime.TotalMinutes, 36, 42);
    }

    [Fact]
    public void BatteryHistoryService_FormatsDurationCleanly()
    {
        Assert.Equal("~1 min", BatteryHistoryService.FormatDuration(TimeSpan.FromMinutes(1), AppLanguage.English));
        Assert.Equal("~12 min", BatteryHistoryService.FormatDuration(TimeSpan.FromMinutes(12), AppLanguage.English));
        Assert.Equal("~45 min", BatteryHistoryService.FormatDuration(TimeSpan.FromMinutes(44), AppLanguage.English));
        Assert.Equal("~1h 15m", BatteryHistoryService.FormatDuration(TimeSpan.FromMinutes(74), AppLanguage.English));
        Assert.Equal("~14h", BatteryHistoryService.FormatDuration(TimeSpan.FromHours(14.2), AppLanguage.English));

        Assert.Equal("~1 мин", BatteryHistoryService.FormatRussianDuration(TimeSpan.FromMinutes(1)));
        Assert.Equal("~12 мин", BatteryHistoryService.FormatRussianDuration(TimeSpan.FromMinutes(12)));
        Assert.Equal("~45 мин", BatteryHistoryService.FormatRussianDuration(TimeSpan.FromMinutes(44)));
        Assert.Equal("~1 ч 15 мин", BatteryHistoryService.FormatRussianDuration(TimeSpan.FromMinutes(74)));
        Assert.Equal("~14 ч", BatteryHistoryService.FormatRussianDuration(TimeSpan.FromHours(14.2)));
    }

    [Fact]
    public void TrayIconStyleJsonConverter_MigratesLegacyEnumNamesWithoutResettingConfig()
    {
        var legacyJson = """{"TrayIconStyle": "NumberAndSilhouette"}""";
        var cfg1 = JsonSerializer.Deserialize<AppConfig>(legacyJson);
        Assert.NotNull(cfg1);
        Assert.Equal(TrayIconStyle.TrayLargePercent, cfg1!.TrayIconStyle);

        var legacyDual = """{"TrayIconStyle": "DualMouseAndPercent"}""";
        var cfg2 = JsonSerializer.Deserialize<AppConfig>(legacyDual);
        Assert.Equal(TrayIconStyle.TaskbarPillIconAndPercent, cfg2!.TrayIconStyle);
    }

    [Fact]
    public async Task FullBatteryAlert_TriggersOnceAt100PercentOnCable()
    {
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol { SimulatedBatteryPercent = 96, SimulatedCharging = true };
        var registry = new ProtocolRegistry([virtualProto]);
        await using var coordinator = new DeviceCoordinator(new EmptyTransport(), registry, store, virtualProto);

        int fullAlerts = 0;
        coordinator.FullBatteryAlertTriggered += _ => fullAlerts++;

        await coordinator.UpdateConfigAsync(c => { c.EnableVirtualMouse = true; c.FullBatteryAlertEnabled = true; });
        await coordinator.RefreshDevicesAsync();
        Assert.Equal(0, fullAlerts);

        // Hits 100% while charging -> fires once
        virtualProto.SimulatedBatteryPercent = 100;
        await coordinator.PollBatteryAsync();
        Assert.Equal(1, fullAlerts);

        // Stays 100% -> does NOT spam
        await coordinator.PollBatteryAsync();
        Assert.Equal(1, fullAlerts);
    }

    [Fact]
    public async Task GenericWiredMouse_StaysActiveAndNonDestructiveWithoutBatterySupport()
    {
        var wiredMouse = new MouseDeviceInfo
        {
            DevicePath = "hid://office_mouse",
            VendorId = 0x045E,
            ProductId = 0x00CB,
            UsagePage = 0x0001,
            Usage = 0x0002,
            ProductName = "Microsoft Basic Optical Mouse"
        };
        var transport = new SingleDeviceTransport(wiredMouse);
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol();
        var registry = new ProtocolRegistry([new CompXProtocol(), new GenericMouseProtocol(), virtualProto]);
        await using var coordinator = new DeviceCoordinator(transport, registry, store, virtualProto);

        await coordinator.RefreshDevicesAsync();

        Assert.Equal("Microsoft Basic Optical Mouse", coordinator.State.DeviceName);
        Assert.Equal(MouseConnectionStatus.Active, coordinator.State.Status);
        Assert.Equal(MouseTransportKind.WiredUsb, coordinator.State.Transport);
        Assert.Null(coordinator.State.BatteryPercent);
        Assert.False(coordinator.ActiveCapabilities.SupportsHardwareSettings);
    }

    [Fact]
    public void GenericMouseProtocol_RejectsCompoundGamingKeyboardsAndHeadsets()
    {
        var proto = new GenericMouseProtocol();

        var gamingKeyboard = new MouseDeviceInfo
        {
            DevicePath = "hid://gaming_kb_mouse_col",
            VendorId = 0x1A2C,
            ProductId = 0x4C5E,
            UsagePage = 0x0001,
            Usage = 0x0002,
            ProductName = "Gaming Keyboard"
        };
        var realMouse = gamingKeyboard with { ProductName = "USB Optical Mouse" };
        var comboReceiver = gamingKeyboard with { ProductName = "Wireless Keyboard and Mouse Combo" };

        Assert.Equal(0, proto.MatchScore(gamingKeyboard));
        Assert.Equal(15, proto.MatchScore(realMouse));
        Assert.Equal(15, proto.MatchScore(comboReceiver));
    }

    [Theory]
    [InlineData(125, 0x08)]
    [InlineData(250, 0x04)]
    [InlineData(500, 0x02)]
    [InlineData(1000, 0x01)]
    [InlineData(2000, 0x10)]
    [InlineData(4000, 0x20)]
    [InlineData(8000, 0x40)]
    public void CompX_PollingRateEncodeDecode_RoundTripsAllRates(int hz, byte code)
    {
        Assert.Equal(code, CompXProtocol.EncodePollingRate(hz));
        Assert.Equal(hz, CompXProtocol.DecodePollingRate(code));
    }

    [Fact]
    public void CompX_Matches65ByteFirmwareVariant()
    {
        var proto = new CompXProtocol();
        var dev65 = new MouseDeviceInfo
        {
            DevicePath = "hid://compx65",
            VendorId = 0x25A7,
            ProductId = 0xFA70,
            UsagePage = 0xFF02,
            Usage = 0x0002,
            OutputReportLength = 65,
            InputReportLength = 65,
            CanOpenReadWrite = true,
            ProductName = "MCHOSE A5 Pro"
        };
        Assert.True(proto.MatchScore(dev65) > 60);
    }

    [Fact]
    public void BluetoothMouseProtocol_MatchesConnectedBluetoothAndSkipsUsb()
    {
        var proto = new BluetoothMouseProtocol();
        var bt = new MouseDeviceInfo
        {
            DevicePath = "bt://dev1",
            ProductName = "Keychron M3 BT",
            IsBluetooth = true,
            IsBluetoothConnected = true,
            OsBatteryPercent = 72,
            CanOpenReadWrite = true
        };
        var usb = new MouseDeviceInfo
        {
            DevicePath = "hid://usb1",
            VendorId = 0x373B,
            ProductId = 0x1040,
            UsagePage = 0xFF02,
            CanOpenReadWrite = true
        };

        Assert.True(proto.MatchScore(bt) >= 100);
        Assert.Equal(0, proto.MatchScore(usb));
        Assert.Equal(MouseTransportKind.Bluetooth, proto.DetectTransport(bt));
    }

    [Fact]
    public void LogitechHidpp_RejectsNonLogitechAndNonVendorCollections()
    {
        var proto = new LogitechHidPlusPlusProtocol();
        var logiMouse = new MouseDeviceInfo
        {
            DevicePath = "hid://gpx2",
            VendorId = 0x046D,
            ProductId = 0xC54D,
            UsagePage = 0xFF00,
            Usage = 0x0002,
            OutputReportLength = 20,
            InputReportLength = 20,
            CanOpenReadWrite = true,
            ProductName = "PRO X 2 LIGHTSPEED Receiver"
        };
        var logiStdPointer = logiMouse with { UsagePage = 0x0001, Usage = 0x0002 };

        Assert.True(proto.MatchScore(logiMouse) >= 100);
        Assert.Equal(0, proto.MatchScore(logiStdPointer));
        Assert.Equal(MouseTransportKind.Wireless24G, proto.DetectTransport(logiMouse));
    }

    [Fact]
    public void RazerHid_Matches90ByteFeatureInterfaceAndDetectsTransport()
    {
        var proto = new RazerHidProtocol();
        var viper = new MouseDeviceInfo
        {
            DevicePath = "hid://viper_v3_pro",
            VendorId = 0x1532,
            ProductId = 0x00C1,
            InterfaceNumber = 0,
            FeatureReportLength = 91,
            CanOpenReadWrite = true,
            ProductName = "Razer HyperPolling Wireless Dongle"
        };
        Assert.True(proto.MatchScore(viper) >= 90);
        Assert.Equal(MouseTransportKind.Wireless24G, proto.DetectTransport(viper));
    }

    [Fact]
    public void BatteryHistoryService_ReturnsNullDrainWhenChargingOrInsufficientSpan()
    {
        var cache = new CachedDeviceBattery();
        var now = DateTimeOffset.UtcNow;
        BatteryHistoryService.RecordPoint(cache, 50, true, now.AddHours(-1));
        BatteryHistoryService.RecordPoint(cache, 80, true, now);
        Assert.Null(BatteryHistoryService.ComputeMeasuredDrainRatePerHour(cache.History));
    }

    [Fact]
    public void BatteryHistoryService_PrunesOldPointsBeyond24Hours()
    {
        var cache = new CachedDeviceBattery();
        var now = DateTimeOffset.UtcNow;
        cache.History.Add(new BatteryHistoryPoint(now.AddHours(-30), 99, false));
        BatteryHistoryService.RecordPoint(cache, 40, false, now);
        Assert.Single(cache.History);
        Assert.Equal(40, cache.History[0].Percent);
    }

    [Fact]
    public async Task DeviceCoordinator_SelectDeviceAsync_PersistsOverrideAndSwitchesActiveMouse()
    {
        var m1 = new MouseDeviceInfo { DevicePath = "hid://m1", VendorId = 0x045E, ProductId = 0x0001, UsagePage = 0x0001, Usage = 0x0002, ProductName = "Mouse One" };
        var m2 = new MouseDeviceInfo { DevicePath = "bt://m2", ProductName = "Mouse Two BT", SerialNumber = "112233", IsBluetooth = true, IsBluetoothConnected = true, OsBatteryPercent = 88, CanOpenReadWrite = true };
        var transport = new MultiDeviceTransport([m1], [m2]);
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol();
        var registry = new ProtocolRegistry([new BluetoothMouseProtocol(), new GenericMouseProtocol(), virtualProto]);
        await using var coordinator = new DeviceCoordinator(transport, registry, store, virtualProto);

        await coordinator.RefreshDevicesAsync();
        Assert.Equal(2, coordinator.State.AvailableMice.Count);
        Assert.Equal("bt://m2", coordinator.State.DevicePath); // Bluetooth with battery outranks generic OS mouse

        await coordinator.SelectDeviceAsync("hid://m1");
        Assert.Equal("hid://m1", coordinator.State.DevicePath);
        Assert.Equal("Mouse One", coordinator.State.DeviceName);
    }

    private sealed class MultiDeviceTransport(IReadOnlyList<MouseDeviceInfo> usb, IReadOnlyList<MouseDeviceInfo> bt) : IHidTransport
    {
        public IReadOnlyList<MouseDeviceInfo> EnumerateDevices() => usb;
        public Task<IReadOnlyList<MouseDeviceInfo>> EnumerateBluetoothMiceAsync(CancellationToken ct = default) => Task.FromResult(bt);
        public Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo device, CancellationToken ct = default) => Task.FromResult<IHidSession?>(null);
    }

    private sealed class EmptyTransport : IHidTransport
    {
        public IReadOnlyList<MouseDeviceInfo> EnumerateDevices() => [];
        public Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo device, CancellationToken ct = default) => Task.FromResult<IHidSession?>(null);
    }

    private sealed class SingleDeviceTransport(MouseDeviceInfo device) : IHidTransport
    {
        public IReadOnlyList<MouseDeviceInfo> EnumerateDevices() => [device];
        public Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo d, CancellationToken ct = default) => Task.FromResult<IHidSession?>(null);
    }

    private sealed class QueuedMockSession(IEnumerable<byte[]> responses) : IHidSession
    {
        private readonly Queue<byte[]> _queue = new(responses);
        public MouseDeviceInfo Device { get; } = new() { DevicePath = "mock://q" };
        public bool IsOpen => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default) => Task.FromResult(true);
        public Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default) => Task.FromResult(true);
        public Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default)
            => Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : null);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
