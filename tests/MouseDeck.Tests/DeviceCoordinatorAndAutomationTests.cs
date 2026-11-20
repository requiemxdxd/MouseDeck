using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Services;
using MouseDeck.Core.Transport;
using Xunit;

namespace MouseDeck.Tests;

public sealed class DeviceCoordinatorAndAutomationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MouseDeckTests_" + Guid.NewGuid().ToString("N"));

    private sealed class EmptyHidTransport : IHidTransport
    {
        public IReadOnlyList<MouseDeviceInfo> EnumerateDevices() => [];
        public Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo device, CancellationToken ct = default) => Task.FromResult<IHidSession?>(null);
    }

    [Fact]
    public async Task SleepMode_CachesLastBattery_And_FlushesWriteOnlyProfileOnWake()
    {
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol { SimulatedBatteryPercent = 76, SimulatedSleeping = false };
        var registry = new ProtocolRegistry([virtualProto]);
        await using var coordinator = new DeviceCoordinator(new EmptyHidTransport(), registry, store, virtualProto);

        // Enable virtual mouse & refresh
        await coordinator.UpdateConfigAsync(c => c.EnableVirtualMouse = true);
        await coordinator.RefreshDevicesAsync();

        Assert.Equal(MouseConnectionStatus.Active, coordinator.State.Status);
        Assert.Equal(76, coordinator.State.BatteryPercent);

        // Mouse enters deep sleep
        virtualProto.SimulatedSleeping = true;
        await coordinator.PollBatteryAsync();

        Assert.Equal(MouseConnectionStatus.Sleeping, coordinator.State.Status);
        Assert.Equal(76, coordinator.State.BatteryPercent); // Cached % retained, NOT 0 or error!

        // User switches to Gaming profile while mouse is asleep -> deferred write-only sync
        await coordinator.SelectProfileAsync("gaming", ProfileSwitchSource.Manual);
        Assert.Equal("gaming", coordinator.State.ActiveProfileId);
        Assert.True(coordinator.State.HasPendingSync);

        // Mouse wakes up -> coordinator automatically flushes pending profile!
        virtualProto.SimulatedSleeping = false;
        virtualProto.SimulatedBatteryPercent = 75;
        await coordinator.PollBatteryAsync();

        Assert.Equal(MouseConnectionStatus.Active, coordinator.State.Status);
        Assert.Equal(75, coordinator.State.BatteryPercent);
        Assert.False(coordinator.State.HasPendingSync);
        Assert.NotNull(coordinator.State.LastSyncUtc);
    }

    [Fact]
    public async Task LowBatteryAlert_RespectsThresholdAndHysteresis()
    {
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol { SimulatedBatteryPercent = 25, SimulatedSleeping = false };
        var registry = new ProtocolRegistry([virtualProto]);
        await using var coordinator = new DeviceCoordinator(new EmptyHidTransport(), registry, store, virtualProto);

        int alertCount = 0;
        coordinator.LowBatteryAlertTriggered += (_, _) => alertCount++;

        await coordinator.UpdateConfigAsync(c => c.EnableVirtualMouse = true);
        await coordinator.RefreshDevicesAsync();
        Assert.Equal(0, alertCount);

        // Drops to 14% -> fires once
        virtualProto.SimulatedBatteryPercent = 14;
        await coordinator.PollBatteryAsync();
        Assert.Equal(1, alertCount);

        // Drops further to 12% -> does NOT spam
        virtualProto.SimulatedBatteryPercent = 12;
        await coordinator.PollBatteryAsync();
        Assert.Equal(1, alertCount);

        // Plugged in charging -> resets hysteresis
        virtualProto.SimulatedCharging = true;
        await coordinator.PollBatteryAsync();
        Assert.Equal(MouseConnectionStatus.Charging, coordinator.State.Status);

        // Unplugged and drops to 13% again -> fires again
        virtualProto.SimulatedCharging = false;
        virtualProto.SimulatedBatteryPercent = 13;
        await coordinator.PollBatteryAsync();
        Assert.Equal(2, alertCount);
    }

    [Fact]
    public async Task Startup_IsStrictlyReadOnly_ReadsDeviceSettingsWithoutOverwriting()
    {
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol { SimulatedBatteryPercent = 91, SimulatedSleeping = false };
        var registry = new ProtocolRegistry([virtualProto]);
        await using var coordinator = new DeviceCoordinator(new EmptyHidTransport(), registry, store, virtualProto);

        var txLogs = new List<HidPacketLogEntry>();
        coordinator.PacketLogged += entry =>
        {
            if (entry.Direction == PacketDirection.Tx)
                txLogs.Add(entry);
        };

        await coordinator.UpdateConfigAsync(c => c.EnableVirtualMouse = true);
        await coordinator.RefreshDevicesAsync();

        // Must have read existing mouse settings (2000 Hz, 1600 DPI) without sending a single write packet!
        Assert.Equal(HardwareConfigOrigin.ReadFromDevice, coordinator.State.ConfigOrigin);
        Assert.Equal(2000, coordinator.State.CurrentPollingRateHz);
        Assert.Equal(1600, coordinator.State.CurrentDpi);
        Assert.False(coordinator.State.HasPendingSync);
        Assert.Empty(txLogs);
    }

    [Fact]
    public async Task ProfileAutomation_SwitchesByProcessAndHotkeys()
    {
        var store = new JsonConfigStore(_tempDir);
        var virtualProto = new VirtualMockProtocol { SimulatedBatteryPercent = 80 };
        var registry = new ProtocolRegistry([virtualProto]);
        await using var coordinator = new DeviceCoordinator(new EmptyHidTransport(), registry, store, virtualProto);
        using var automation = new ProfileAutomationService(coordinator, TimeSpan.Zero);

        await coordinator.UpdateConfigAsync(c =>
        {
            c.EnableVirtualMouse = true;
            c.AutoSwitchEnabled = true;
        });
        await coordinator.RefreshDevicesAsync();

        // Locked by default -> auto-switch must NOT overwrite mouse settings
        await automation.EvaluateNowAsync("CS2.EXE");
        Assert.Equal("device-read", coordinator.State.ActiveProfileId);

        // Unlock write lock -> auto-switch works
        coordinator.SetWriteUnlocked(true);

        // Foreground game cs2.exe -> switches to Gaming profile
        await automation.EvaluateNowAsync("CS2.EXE");
        Assert.Equal("gaming", coordinator.State.ActiveProfileId);
        Assert.Equal(ProfileSwitchSource.AutoProcess, coordinator.State.ProfileSource);

        // Switch back to browser -> returns to Eco desktop profile
        await automation.EvaluateNowAsync("chrome.exe");
        Assert.Equal("eco", coordinator.State.ActiveProfileId);

        // Manual override blocks auto-switching
        await coordinator.UpdateConfigAsync(c => c.ManualProfileOverride = true);
        await automation.EvaluateNowAsync("cs2.exe");
        Assert.Equal("eco", coordinator.State.ActiveProfileId);

        // Hotkey CycleProfile advances eco -> gaming -> esports
        await automation.HandleHotkeyActionAsync("CycleProfile");
        Assert.Equal("gaming", coordinator.State.ActiveProfileId);
        Assert.Equal(ProfileSwitchSource.Hotkey, coordinator.State.ProfileSource);

        await automation.HandleHotkeyActionAsync("ToggleEcoGaming");
        Assert.Equal("eco", coordinator.State.ActiveProfileId);
    }

    [Fact]
    public void JsonConfigStore_RoundTripsAtomicSaveAndLoad()
    {
        var store = new JsonConfigStore(_tempDir);
        var cfg = store.Load();
        Assert.True(store.WasCreatedOnLastLoad);

        cfg.LowBatteryThresholdPercent = 12;
        cfg.ProcessRules.Add(new ProcessProfileRule { ProcessName = "overwatch.exe", ProfileId = "esports" });
        store.Save(cfg);

        var reloaded = store.Load();
        Assert.False(store.WasCreatedOnLastLoad);
        Assert.Equal(12, reloaded.LowBatteryThresholdPercent);
        Assert.Contains(reloaded.ProcessRules, r => r.ProcessName == "overwatch.exe" && r.ProfileId == "esports");
    }

    [Fact]
    public void JsonTemplateProtocol_LoadsCustomJsonFileAndGeneratesSample()
    {
        var protoDir = Path.Combine(_tempDir, "protocols");
        Directory.CreateDirectory(protoDir);
        File.WriteAllText(Path.Combine(protoDir, "custom.json"), """
        {
          "ProtocolId": "example-paw3395-mouse",
          "DisplayName": "Example PAW3395 Wireless",
          "VendorIdHex": "0x1D57",
          "ProductIdHex": "0xFA60",
          "UsagePageHex": "0xFF02",
          "UseFeatureReports": true,
          "ReportLength": 17,
          "ReportId": 8,
          "Checksum": "CompX55",
          "BatteryQueryHex": "08 04 00 00 00 00",
          "BatteryPercentOffset": 6,
          "ChargingFlagOffset": 7,
          "VoltageHighOffset": 8,
          "VoltageLowOffset": 9,
          "SupportedPollingRatesHz": [125, 250, 500, 1000, 2000, 4000],
          "MaxDpi": 26000
        }
        """);

        var loaded = JsonTemplateProtocol.LoadFromDirectory(protoDir);
        Assert.Single(loaded);
        Assert.Equal("example-paw3395-mouse", loaded[0].ProtocolId);
        Assert.True(File.Exists(Path.Combine(protoDir, "_example_custom_mouse.json.sample")));

        var matchingDev = new MouseDeviceInfo
        {
            DevicePath = "hid://custom",
            VendorId = 0x1D57,
            ProductId = 0xFA60,
            UsagePage = 0xFF02,
            CanOpenReadWrite = true
        };
        Assert.Equal(140, loaded[0].MatchScore(matchingDev));
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
