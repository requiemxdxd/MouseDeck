using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Services;

/// <summary>
/// Manages USB HID &amp; Bluetooth mouse lifecycle, multi-mouse selection, read-only startup discovery,
/// sleep battery caching, 24h drain-rate history, vendor-software coexistence, and explicit profile sync.
/// STRICT CONTRACT: Never overwrites mouse settings on startup or initial connection;
/// only reads current device state until the user explicitly changes or applies a setting.
/// </summary>
public sealed class DeviceCoordinator : IAsyncDisposable
{
    private readonly IHidTransport _transport;
    private readonly ProtocolRegistry _registry;
    private readonly JsonConfigStore _configStore;
    private readonly VirtualMockProtocol _virtualProtocol;
    private readonly SemaphoreSlim _hidLock = new(1, 1);

    private AppConfig _config;
    private MouseDeviceInfo? _activeDevice;
    private IMouseProtocol? _activeProtocol;
    private MouseRuntimeState _state;
    private MouseProfile? _lastHardwareSnapshot;
    private bool _isWriteUnlocked;
    private bool _lowBatteryAlertSent;
    private bool _fullBatteryAlertSent;
    private bool _hasReadInitialDeviceConfig;
    private bool _isSystemSuspended;
    private DateTimeOffset _lastPollUtc = DateTimeOffset.MinValue;
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private Func<TimeSpan>? _getUserIdleTime;

    public DeviceCoordinator(
        IHidTransport transport,
        ProtocolRegistry registry,
        JsonConfigStore configStore,
        VirtualMockProtocol virtualProtocol)
    {
        _transport = transport;
        _registry = registry;
        _configStore = configStore;
        _virtualProtocol = virtualProtocol;
        _config = _configStore.Load();
        _isWriteUnlocked = _config.UnlockEditingOnStartup;

        var activeProfile = GetActiveProfile();
        _state = new MouseRuntimeState
        {
            ActiveProfileId = activeProfile.Id,
            ActiveProfileName = activeProfile.Name,
            CurrentDpi = activeProfile.CurrentDpi,
            CurrentPollingRateHz = activeProfile.PollingRateHz,
            ProfileSource = ProfileSwitchSource.Default,
            ConfigOrigin = HardwareConfigOrigin.NotReadPreserved,
            HasPendingSync = false
        };
    }

    public event Action<MouseRuntimeState>? StateChanged;
    public event Action<HidPacketLogEntry>? PacketLogged;
    public event Action<string, int>? LowBatteryAlertTriggered;
    public event Action<string>? FullBatteryAlertTriggered;
    public event Action<AppConfig>? ConfigChanged;

    public AppConfig Config => _config;
    public MouseRuntimeState State => _state;
    public MouseCapabilities ActiveCapabilities => _activeProtocol?.Capabilities ?? MouseCapabilities.GenericOsMouse;
    public VirtualMockProtocol VirtualMouse => _virtualProtocol;
    public bool IsWriteUnlocked => _isWriteUnlocked;
    public MouseProfile? LastHardwareSnapshot => _lastHardwareSnapshot;

    public void SetWriteUnlocked(bool unlocked)
    {
        _isWriteUnlocked = unlocked;
    }

    public void ConfigureUserIdleProvider(Func<TimeSpan> getUserIdleTime)
    {
        _getUserIdleTime = getUserIdleTime;
    }

    public MouseProfile GetActiveProfile()
    {
        return _config.Profiles.FirstOrDefault(p => p.Id == _config.ActiveProfileId)
               ?? _config.Profiles.First();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await RefreshDevicesAsync(_cts.Token);

        // Adaptive tick every 30s; actual HID I/O only fires when adaptive interval (60s/120s/180s) elapses
        _pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _pollLoopTask = RunPollLoopAsync(_cts.Token);
    }

    public void OnSystemSuspended()
    {
        _isSystemSuspended = true;
    }

    public async Task OnSystemResumedAsync(CancellationToken ct = default)
    {
        _isSystemSuspended = false;
        try
        {
            // Give USB/2.4GHz dongles and Bluetooth radios 2.5 seconds to re-enumerate cleanly after wake
            await Task.Delay(2500, ct);
            await RefreshDevicesAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        if (_pollTimer is null) return;
        try
        {
            while (await _pollTimer.WaitForNextTickAsync(ct))
            {
                if (_isSystemSuspended || _activeDevice is null)
                    continue;

                var vendorApp = _transport.DetectRunningVendorSoftware();
                var idle = _getUserIdleTime?.Invoke() ?? TimeSpan.Zero;

                // Adaptive polling interval:
                // - Charging on USB: 30s (60s if vendor software running) for accurate real-time ETA calibration
                // - Sleeping & user idle > 3m: 180s
                // - Vendor software running (G HUB / Synapse / ATK HUB): 120s (extra gentle coexistence)
                // - Normal active use: 60s
                TimeSpan interval = _state.IsCharging
                    ? (!string.IsNullOrWhiteSpace(vendorApp) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30))
                    : (_state.Status == MouseConnectionStatus.Sleeping && idle > TimeSpan.FromMinutes(3))
                        ? TimeSpan.FromSeconds(180)
                        : !string.IsNullOrWhiteSpace(vendorApp)
                            ? TimeSpan.FromSeconds(120)
                            : TimeSpan.FromSeconds(60);

                if ((DateTimeOffset.UtcNow - _lastPollUtc) < interval)
                    continue;

                await PollBatteryAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public IReadOnlyList<MouseDeviceInfo> ScanAllDevices()
    {
        var list = new List<MouseDeviceInfo>(_transport.EnumerateDevices());
        if (_config.EnableVirtualMouse)
        {
            list.Insert(0, VirtualMockProtocol.VirtualDevice);
        }
        return list;
    }

    public async Task<IReadOnlyList<MouseDeviceInfo>> ScanAllDevicesAsync(CancellationToken ct = default)
    {
        var list = new List<MouseDeviceInfo>(_transport.EnumerateDevices());

        try
        {
            var btMice = await _transport.EnumerateBluetoothMiceAsync(ct);
            list.AddRange(btMice);
        }
        catch
        {
            // Bluetooth adapter may be absent or powered off — silently ignore
        }

        if (_config.EnableVirtualMouse)
        {
            list.Insert(0, VirtualMockProtocol.VirtualDevice);
        }
        return list;
    }

    public async Task SelectDeviceAsync(string? devicePath, CancellationToken ct = default)
    {
        _config.SelectedDevicePathOverride = string.IsNullOrWhiteSpace(devicePath) ? null : devicePath;
        await _configStore.SaveAsync(_config, ct);
        ConfigChanged?.Invoke(_config);
        await RefreshDevicesAsync(ct);
    }

    public async Task RefreshDevicesAsync(CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            var vendorApp = _transport.DetectRunningVendorSoftware();
            var devices = await ScanAllDevicesAsync(ct);
            var allMatches = _registry.FindAllMatchedMice(devices, _config.SelectedDevicePathOverride);
            var match = allMatches.FirstOrDefault();

            var summaries = allMatches.Select(m => new DiscoveredMouseSummary(
                m.Device.DevicePath,
                m.Device.DeviceKey,
                string.IsNullOrWhiteSpace(m.Device.ProductName) ? $"Mouse {m.Device.DeviceKey}" : m.Device.ProductName,
                m.Protocol.ProtocolId,
                m.Protocol.DisplayName,
                m.Protocol.DetectTransport(m.Device),
                m.Protocol.Capabilities.SupportsBatteryRead,
                m.Protocol.Capabilities.SupportsHardwareSettings,
                match is not null && m.Device.DevicePath.Equals(match.Device.DevicePath, StringComparison.OrdinalIgnoreCase)
            )).ToList();

            if (match is null)
            {
                _activeDevice = null;
                _activeProtocol = null;
                _hasReadInitialDeviceConfig = false;
                var profile = GetActiveProfile();
                UpdateState(_state with
                {
                    DeviceName = "No mouse detected",
                    DeviceKey = string.Empty,
                    DevicePath = string.Empty,
                    DeviceInterfaceInfo = "—",
                    ProtocolId = string.Empty,
                    ProtocolName = "No driver",
                    Status = MouseConnectionStatus.Disconnected,
                    Transport = MouseTransportKind.Unknown,
                    BatteryPercent = null,
                    VoltageMv = null,
                    IsCharging = false,
                    MeasuredDrainPercentPerHour = null,
                    BatteryHistory = [],
                    HasPendingSync = false,
                    ConfigOrigin = HardwareConfigOrigin.NotReadPreserved,
                    ActiveProfileId = profile.Id,
                    ActiveProfileName = profile.Name,
                    CurrentDpi = profile.CurrentDpi,
                    CurrentPollingRateHz = profile.PollingRateHz,
                    DetectedVendorSoftware = vendorApp,
                    AvailableMice = summaries
                });
                return;
            }

            bool deviceChanged = _activeDevice?.DevicePath != match.Device.DevicePath;
            _activeDevice = match.Device;
            _activeProtocol = match.Protocol;

            var cached = GetOrCreateDeviceCache(match.Device);
            var activeProfile = GetActiveProfile();

            if (deviceChanged)
            {
                _hasReadInitialDeviceConfig = false;

                EmitLog(new HidPacketLogEntry(
                    DateTimeOffset.UtcNow,
                    PacketDirection.Info,
                    "PnP",
                    string.Empty,
                    $"Device detected (read-only): {match.Device.ProductName} ({match.Device.DeviceKey}, {match.Device.UsageHex}) -> {match.Protocol.DisplayName}"));

                UpdateState(_state with
                {
                    DeviceName = string.IsNullOrWhiteSpace(match.Device.ProductName) ? $"Mouse {match.Device.DeviceKey}" : match.Device.ProductName,
                    DeviceKey = match.Device.DeviceKey,
                    DevicePath = match.Device.DevicePath,
                    DeviceInterfaceInfo = $"{match.Device.UsageHex} • {match.Device.InterfaceLabel}",
                    ProtocolId = match.Protocol.ProtocolId,
                    ProtocolName = match.Protocol.DisplayName,
                    Status = match.Protocol.Capabilities.SupportsBatteryRead ? MouseConnectionStatus.Connecting : MouseConnectionStatus.Active,
                    Transport = match.Protocol.DetectTransport(match.Device),
                    BatteryPercent = match.Protocol.Capabilities.SupportsBatteryRead ? cached.LastKnownPercent : null,
                    VoltageMv = match.Protocol.Capabilities.SupportsBatteryRead ? cached.LastKnownVoltageMv : null,
                    LastBatteryUpdateUtc = cached.LastUpdatedUtc,
                    MeasuredDrainPercentPerHour = BatteryHistoryService.ComputeMeasuredDrainRatePerHour(cached.History),
                    BatteryHistory = cached.History.ToList(),
                    HasPendingSync = false, // NEVER auto-overwrite on connect!
                    ConfigOrigin = HardwareConfigOrigin.NotReadPreserved,
                    ActiveProfileId = activeProfile.Id,
                    ActiveProfileName = activeProfile.Name,
                    CurrentDpi = activeProfile.CurrentDpi,
                    CurrentPollingRateHz = activeProfile.PollingRateHz,
                    DetectedVendorSoftware = vendorApp,
                    AvailableMice = summaries
                });
            }
            else
            {
                UpdateState(_state with
                {
                    DetectedVendorSoftware = vendorApp,
                    AvailableMice = summaries
                });
            }

            await PollBatteryInternalAsync(ct);
        }
        finally
        {
            _hidLock.Release();
        }
    }

    public async Task PollBatteryAsync(CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            await PollBatteryInternalAsync(ct);
        }
        finally
        {
            _hidLock.Release();
        }
    }

    public async Task<bool> ReadConfigFromActiveDeviceAsync(CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            if (_activeDevice is null || _activeProtocol is null)
                return false;

            await using var session = await OpenSessionInternalAsync(_activeDevice, ct);
            if (session is null)
                return false;

            return await TryReadHardwareConfigInternalAsync(session, ct);
        }
        finally
        {
            _hidLock.Release();
        }
    }

    private async Task<bool> TryReadHardwareConfigInternalAsync(IHidSession session, CancellationToken ct)
    {
        if (_activeProtocol is null) return false;
        try
        {
            var hardwareProfile = await _activeProtocol.ReadConfigAsync(session, EmitLog, ct);
            if (hardwareProfile is null)
            {
                if (_activeProtocol.Capabilities.SupportsHardwareSettings)
                {
                    EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Info, "Read", string.Empty,
                        "Device is write-only or did not return registers — current mouse settings preserved without overwriting."));
                }
                return false;
            }

            // Store or update the read-only live hardware snapshot profile without writing back to hardware
            var existingIdx = _config.Profiles.FindIndex(p => p.Id == "device-read");
            if (existingIdx >= 0)
            {
                _config.Profiles[existingIdx] = hardwareProfile;
            }
            else
            {
                _config.Profiles.Insert(0, hardwareProfile);
            }

            _config.ActiveProfileId = hardwareProfile.Id;
            _lastHardwareSnapshot = hardwareProfile.Clone();
            _hasReadInitialDeviceConfig = true;

            UpdateState(_state with
            {
                ConfigOrigin = HardwareConfigOrigin.ReadFromDevice,
                HasPendingSync = false,
                ActiveProfileId = hardwareProfile.Id,
                ActiveProfileName = hardwareProfile.Name,
                CurrentDpi = hardwareProfile.CurrentDpi,
                CurrentPollingRateHz = hardwareProfile.PollingRateHz,
                ProfileSource = ProfileSwitchSource.Default
            });

            ConfigChanged?.Invoke(_config);
            await _configStore.SaveAsync(_config, ct);
            return true;
        }
        catch (Exception ex)
        {
            EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, "Read", string.Empty, ex.Message));
            return false;
        }
    }

    private async Task PollBatteryInternalAsync(CancellationToken ct)
    {
        if (_activeDevice is null || _activeProtocol is null)
            return;

        _lastPollUtc = DateTimeOffset.UtcNow;
        var vendorApp = _transport.DetectRunningVendorSoftware();

        // Standard wired/generic mouse without battery telemetry stays cleanly Active
        if (!_activeProtocol.Capabilities.SupportsBatteryRead && !_activeDevice.IsHidBatteryCollection)
        {
            UpdateState(_state with
            {
                Status = MouseConnectionStatus.Active,
                Transport = _activeProtocol.DetectTransport(_activeDevice),
                BatteryPercent = null,
                VoltageMv = null,
                IsCharging = false,
                DetectedVendorSoftware = vendorApp,
                LastSyncError = null
            });
            return;
        }

        // For Bluetooth mice, refresh OS-reported PnP battery value seamlessly
        if (_activeDevice.IsBluetooth)
        {
            try
            {
                var refreshedBt = await _transport.EnumerateBluetoothMiceAsync(ct);
                var updated = refreshedBt.FirstOrDefault(b => b.DevicePath.Equals(_activeDevice.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (updated is not null)
                    _activeDevice = updated;
            }
            catch { }
        }

        var cached = GetOrCreateDeviceCache(_activeDevice);
        var activeProfile = GetActiveProfile();

        await using var session = await OpenSessionInternalAsync(_activeDevice, ct);
        if (session is null)
        {
            UpdateState(_state with
            {
                Status = MouseConnectionStatus.Sleeping,
                BatteryPercent = cached.LastKnownPercent ?? _state.BatteryPercent,
                VoltageMv = cached.LastKnownVoltageMv ?? _state.VoltageMv,
                IsCharging = false,
                DetectedVendorSoftware = vendorApp,
                LastSyncError = null
            });
            return;
        }

        BatteryReading? reading = null;
        try
        {
            reading = await _activeProtocol.ReadBatteryAsync(session, EmitLog, ct);
        }
        catch (Exception ex)
        {
            EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, "Battery", string.Empty, ex.Message));
        }

        if (reading is null || !reading.IsMouseAwake)
        {
            UpdateState(_state with
            {
                Status = MouseConnectionStatus.Sleeping,
                BatteryPercent = cached.LastKnownPercent ?? _state.BatteryPercent,
                VoltageMv = cached.LastKnownVoltageMv ?? _state.VoltageMv,
                IsCharging = false,
                DetectedVendorSoftware = vendorApp,
                LastSyncError = null
            });
            return;
        }

        var now = DateTimeOffset.UtcNow;
        cached.LastKnownPercent = reading.Percentage;
        cached.LastKnownVoltageMv = reading.VoltageMv;
        cached.LastUpdatedUtc = now;
        cached.DeviceName = _activeDevice.ProductName;
        BatteryHistoryService.RecordPoint(cached, reading.Percentage, reading.IsCharging, now, reading.VoltageMv);
        double? drainRate = BatteryHistoryService.ComputeMeasuredDrainRatePerHour(cached.History);
        double? chargeRate = BatteryHistoryService.ComputeMeasuredChargeRatePerMinute(cached.History);

        var newStatus = reading.IsCharging ? MouseConnectionStatus.Charging : MouseConnectionStatus.Active;
        var transport = reading.IsCharging && !_activeDevice.IsBluetooth
            ? MouseTransportKind.WiredUsb
            : _activeProtocol.DetectTransport(_activeDevice);

        UpdateState(_state with
        {
            Status = newStatus,
            Transport = transport,
            BatteryPercent = reading.Percentage,
            VoltageMv = reading.VoltageMv,
            IsCharging = reading.IsCharging,
            LastBatteryUpdateUtc = now,
            LastSeenActiveUtc = now,
            MeasuredDrainPercentPerHour = drainRate,
            MeasuredChargePercentPerMinute = chargeRate,
            BatteryHistory = cached.History.ToList(),
            DetectedVendorSoftware = vendorApp,
            LastSyncError = null
        });

        EvaluateBatteryAlerts(reading);

        // On initial awake connection, ONLY READ current settings from mouse (never overwrite!)
        if (!_hasReadInitialDeviceConfig && !_state.HasPendingSync)
        {
            _hasReadInitialDeviceConfig = true;
            await TryReadHardwareConfigInternalAsync(session, ct);
        }
        // Only flush to hardware on wake-up if the user explicitly queued a change while sleeping!
        else if (_state.HasPendingSync && _activeProtocol.Capabilities.SupportsHardwareSettings)
        {
            activeProfile = GetActiveProfile();
            EmitLog(new HidPacketLogEntry(
                DateTimeOffset.UtcNow,
                PacketDirection.Info,
                "Sync",
                string.Empty,
                $"Mouse woke up — sending queued profile '{activeProfile.Name}'..."));

            bool synced = await _activeProtocol.ApplyProfileAsync(session, activeProfile, EmitLog, ct, _lastHardwareSnapshot);
            if (synced)
            {
                _lastHardwareSnapshot = activeProfile.Clone();
                cached.LastSyncedProfileHash = activeProfile.ComputeSyncHash();
                UpdateState(_state with
                {
                    HasPendingSync = false,
                    ConfigOrigin = HardwareConfigOrigin.AppliedByUser,
                    LastSyncUtc = DateTimeOffset.UtcNow,
                    LastSyncError = null
                });
            }
            else
            {
                UpdateState(_state with
                {
                    HasPendingSync = true,
                    LastSyncError = "Pending retry"
                });
            }
        }

        await _configStore.SaveAsync(_config, ct);
    }

    public async Task SelectProfileAsync(string profileId, ProfileSwitchSource source, CancellationToken ct = default)
    {
        var profile = _config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return;

        bool changed = _config.ActiveProfileId != profile.Id || _state.ProfileSource != source;
        _config.ActiveProfileId = profile.Id;

        UpdateState(_state with
        {
            ActiveProfileId = profile.Id,
            ActiveProfileName = profile.Name,
            CurrentDpi = profile.CurrentDpi,
            CurrentPollingRateHz = profile.PollingRateHz,
            ProfileSource = source
        });

        if (changed)
        {
            ConfigChanged?.Invoke(_config);
        }

        await ApplyActiveProfileAsync(forceSync: false, ct);
    }

    public async Task UpdateActiveProfileAsync(Action<MouseProfile> mutator, bool syncHardware = true, CancellationToken ct = default)
    {
        var profile = GetActiveProfile();
        mutator(profile);

        UpdateState(_state with
        {
            ActiveProfileId = profile.Id,
            ActiveProfileName = profile.Name,
            CurrentDpi = profile.CurrentDpi,
            CurrentPollingRateHz = profile.PollingRateHz
        });

        ConfigChanged?.Invoke(_config);
        await _configStore.SaveAsync(_config, ct);

        if (syncHardware)
        {
            await ApplyActiveProfileAsync(forceSync: true, ct);
        }
    }

    public async Task UpdateConfigAsync(Action<AppConfig> mutator, CancellationToken ct = default)
    {
        mutator(_config);
        await _configStore.SaveAsync(_config, ct);
        ConfigChanged?.Invoke(_config);
    }

    public void UpdateActiveForegroundProcess(string? processName)
    {
        if (string.Equals(_state.ActiveForegroundProcess, processName, StringComparison.OrdinalIgnoreCase))
            return;

        UpdateState(_state with { ActiveForegroundProcess = processName });
    }

    public async Task ApplyActiveProfileAsync(bool forceSync, CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            var profile = GetActiveProfile();
            await _configStore.SaveAsync(_config, ct);

            if (_activeDevice is null || _activeProtocol is null)
            {
                UpdateState(_state with { HasPendingSync = true });
                return;
            }

            if (!_activeProtocol.Capabilities.SupportsHardwareSettings)
            {
                UpdateState(_state with { HasPendingSync = false });
                return;
            }

            var cached = GetOrCreateDeviceCache(_activeDevice);
            string hash = profile.ComputeSyncHash();

            if (!forceSync && cached.LastSyncedProfileHash == hash && !_state.HasPendingSync)
                return;

            if (_state.Status == MouseConnectionStatus.Sleeping && !forceSync)
            {
                UpdateState(_state with
                {
                    HasPendingSync = true,
                    LastSyncError = null
                });
                EmitLog(new HidPacketLogEntry(
                    DateTimeOffset.UtcNow,
                    PacketDirection.Info,
                    "Sync",
                    string.Empty,
                    $"Mouse is sleeping: changes to profile '{profile.Name}' saved and will be sent on wake."));
                return;
            }

            await using var session = await OpenSessionInternalAsync(_activeDevice, ct);
            if (session is null)
            {
                UpdateState(_state with { HasPendingSync = true });
                return;
            }

            bool ok = await _activeProtocol.ApplyProfileAsync(session, profile, EmitLog, ct, _lastHardwareSnapshot);
            if (ok)
            {
                // Verify hardware registers by reading back when the mouse supports ReadConfig
                var verified = await _activeProtocol.ReadConfigAsync(session, EmitLog, ct);
                _lastHardwareSnapshot = verified?.Clone() ?? profile.Clone();

                cached.LastSyncedProfileHash = profile.ComputeSyncHash();
                await _configStore.SaveAsync(_config, ct);
                UpdateState(_state with
                {
                    HasPendingSync = false,
                    ConfigOrigin = HardwareConfigOrigin.AppliedByUser,
                    LastSyncUtc = DateTimeOffset.UtcNow,
                    LastSyncError = null
                });
            }
            else
            {
                UpdateState(_state with
                {
                    Status = _state.Status == MouseConnectionStatus.Disconnected ? MouseConnectionStatus.Disconnected : MouseConnectionStatus.Sleeping,
                    HasPendingSync = true,
                    LastSyncError = null
                });
            }
        }
        finally
        {
            _hidLock.Release();
        }
    }

    public async Task<BatteryReading?> ProbeDeviceBatteryAsync(MouseDeviceInfo device, IMouseProtocol? protocolOverride = null, CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            var protocol = protocolOverride ?? _registry.FindBestProtocol(device) ?? _registry.Protocols.First();
            await using var session = await OpenSessionInternalAsync(device, ct);
            if (session is null)
            {
                EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, "Scanner", string.Empty,
                    $"Failed to open interface {device.DeviceKey} ({device.UsageHex}) — may be locked by OS."));
                return null;
            }

            return await protocol.ReadBatteryAsync(session, EmitLog, ct);
        }
        finally
        {
            _hidLock.Release();
        }
    }

    public async Task<byte[]?> SendRawPacketAsync(
        MouseDeviceInfo device,
        bool useFeatureReport,
        byte reportId,
        string hexPayload,
        ChecksumAlgorithm checksum,
        bool readResponse,
        CancellationToken ct = default)
    {
        await _hidLock.WaitAsync(ct);
        try
        {
            var raw = HidPacketHelper.ParseHexString(hexPayload);
            int targetLen = useFeatureReport
                ? Math.Max(device.FeatureReportLength, Math.Max(raw.Length, 17))
                : Math.Max(device.OutputReportLength, Math.Max(raw.Length, 17));

            byte[] packet = raw.Length > 0 && raw[0] == reportId
                ? HidPacketHelper.ResizePacket(raw, targetLen)
                : HidPacketHelper.ResizePacket(raw, targetLen, reportId);

            if (checksum != ChecksumAlgorithm.None && packet.Length >= 2)
            {
                packet[^1] = HidPacketHelper.ComputeChecksum(packet.AsSpan(0, packet.Length - 1), checksum);
            }

            string ch = useFeatureReport ? "Feature" : "Interrupt";
            EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, ch, HidPacketHelper.ToHexString(packet),
                $"Raw -> {device.DeviceKey} ({device.UsageHex})"));

            await using var session = await OpenSessionInternalAsync(device, ct);
            if (session is null)
            {
                EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty,
                    "Failed to open HID device (Access Denied or disconnected)."));
                return null;
            }

            if (useFeatureReport)
            {
                bool ok = await session.SetFeatureReportAsync(packet, ct);
                if (!ok)
                {
                    EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty, "HidD_SetFeature returned false."));
                    return null;
                }

                if (!readResponse) return [];

                await Task.Delay(25, ct);
                var rx = await session.GetFeatureReportAsync(reportId, targetLen, ct);
                EmitLog(rx is not null
                    ? new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, ch, HidPacketHelper.ToHexString(rx), "Feature RX")
                    : new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty, "HidD_GetFeature returned null / timeout."));
                return rx;
            }
            else
            {
                session.FlushInputQueue();
                bool ok = await session.WriteOutputReportAsync(packet, 300, ct);
                if (!ok)
                {
                    EmitLog(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty, "WriteFile (Interrupt) returned false / timeout."));
                    return null;
                }

                if (!readResponse) return [];

                int inLen = Math.Max(device.InputReportLength, targetLen);
                var rx = await session.ReadInputReportAsync(inLen, 350, ct);
                EmitLog(rx is not null
                    ? new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, ch, HidPacketHelper.ToHexString(rx), "Interrupt RX")
                    : new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Info, ch, string.Empty, "No incoming reply (350ms timeout)."));
                return rx;
            }
        }
        finally
        {
            _hidLock.Release();
        }
    }

    private Task<IHidSession?> OpenSessionInternalAsync(MouseDeviceInfo device, CancellationToken ct)
    {
        if (device.IsVirtual)
            return Task.FromResult<IHidSession?>(new VirtualMockProtocol.VirtualHidSession());

        return _transport.OpenDeviceAsync(device, ct);
    }

    private CachedDeviceBattery GetOrCreateDeviceCache(MouseDeviceInfo device)
    {
        if (!_config.DeviceCache.TryGetValue(device.DeviceKey, out var cached))
        {
            cached = new CachedDeviceBattery
            {
                DeviceKey = device.DeviceKey,
                DeviceName = device.ProductName
            };
            _config.DeviceCache[device.DeviceKey] = cached;
        }
        cached.History ??= [];
        return cached;
    }

    private void EvaluateBatteryAlerts(BatteryReading reading)
    {
        // 1. Low battery alert with +5% hysteresis
        if (_config.LowBatteryAlertEnabled)
        {
            if (reading.IsCharging || reading.Percentage >= _config.LowBatteryThresholdPercent + 5)
            {
                _lowBatteryAlertSent = false;
            }
            else if (!_lowBatteryAlertSent && reading.Percentage <= _config.LowBatteryThresholdPercent)
            {
                _lowBatteryAlertSent = true;
                LowBatteryAlertTriggered?.Invoke(_state.DeviceName, reading.Percentage);
            }
        }

        // 2. Full charge (100%) notification when charging on cable
        if (_config.FullBatteryAlertEnabled)
        {
            if (!reading.IsCharging || reading.Percentage <= 95)
            {
                _fullBatteryAlertSent = false;
            }
            else if (!_fullBatteryAlertSent && reading.IsCharging && reading.Percentage >= 100)
            {
                _fullBatteryAlertSent = true;
                FullBatteryAlertTriggered?.Invoke(_state.DeviceName);
            }
        }
    }

    private void UpdateState(MouseRuntimeState newState)
    {
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    private void EmitLog(HidPacketLogEntry entry)
    {
        PacketLogged?.Invoke(entry);
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        _pollTimer?.Dispose();
        if (_pollLoopTask is not null)
        {
            try { await _pollLoopTask; } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        _hidLock.Dispose();
    }
}
