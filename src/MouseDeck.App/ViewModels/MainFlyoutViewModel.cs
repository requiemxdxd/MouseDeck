using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Services;
using MouseDeck.Win32.Automation;

namespace MouseDeck.App.ViewModels;

public sealed partial class MainFlyoutViewModel : ObservableObject, IDisposable
{
    // Exact 6 compact polling rates for the main Flyout segmented bar
    private static readonly int[] QuickPollingRates = [125, 500, 1000, 2000, 4000, 8000];
    private static readonly string[] DefaultStageColors = ["#FFEF4444", "#FF38BDF8", "#FF22C55E", "#FFEAB308", "#FFA855F7", "#FFEC4899"];

    private readonly DeviceCoordinator _coordinator;
    private readonly ProtocolRegistry _registry;
    private readonly JsonConfigStore _configStore;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly RawInputPollingTester _pollingTester;

    private bool _isSyncingFromModel;
    private bool _isLoadingWinSettings;
    private MouseProfile? _stagedProfile;
    private string? _stagedProfileId;
    private string? _baselineHash;
    private CancellationTokenSource? _lockedHintCts;

    private static readonly SolidColorBrush BrushGood = CreateBrush(76, 195, 138);
    private static readonly SolidColorBrush BrushWarn = CreateBrush(217, 155, 67);
    private static readonly SolidColorBrush BrushDanger = CreateBrush(229, 72, 77);
    private static readonly SolidColorBrush BrushBright = CreateBrush(237, 237, 237);
    private static readonly SolidColorBrush BrushSegmentEmpty = CreateBrush(35, 35, 39);

    public MainFlyoutViewModel(
        DeviceCoordinator coordinator,
        ProtocolRegistry registry,
        JsonConfigStore configStore,
        GlobalHotkeyService hotkeyService,
        RawInputPollingTester pollingTester)
    {
        _coordinator = coordinator;
        _registry = registry;
        _configStore = configStore;
        _hotkeyService = hotkeyService;
        _pollingTester = pollingTester;

        for (int i = 0; i < 10; i++)
            BatterySegments.Add(new BatterySegmentItemViewModel { Index = i, FillBrush = BrushSegmentEmpty });

        foreach (int hz in QuickPollingRates)
            PollingRates.Add(new PollingRateItemViewModel { RateHz = hz });

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.PacketLogged += OnPacketLogged;
        _coordinator.ConfigChanged += OnCoordinatorConfigChanged;
        _pollingTester.PollingRateUpdated += OnRawPollingRateUpdated;

        AppLocalization.SetLanguage(_coordinator.Config.Language);
        _isFirstRunSetupVisible = !_coordinator.Config.IsFirstRunSetupCompleted;

        _isWriteUnlocked = _coordinator.IsWriteUnlocked;
        _lockBadgeText = _isWriteUnlocked ? Loc.WriteBadge : Loc.ReadBadge;
        if (_isWriteUnlocked)
            InitializeStagingFromCurrent();

        RebuildLocalizedChoiceLists();
        RefreshWindowsSettings();
        RefreshAllFromCoordinator();
    }

    public UiStrings Loc => UiStrings.Instance;

    public event Action<FlyoutBackdropStyle>? BackdropChanged;
    public event Action<TrayIconStyle>? TrayIconStyleChanged;
    public event Action<bool>? PinWindowChanged;
    public event Action<bool>? ModalDialogStateChanged;
    public event Action? ExitRequested;

    private static string P(string ru, string en) => AppLocalization.Pick(ru, en);

    // --- 2-Screen Navigation, Onboarding & Safety Write Lock ---
    [ObservableProperty] private bool _isFirstRunSetupVisible;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private int _settingsTabIndex; // 0=Sensor, 1=Auto, 2=OS & Buttons, 3=HID
    [ObservableProperty] private bool _pinWindowOpen;
    [ObservableProperty] private bool _isWriteUnlocked;
    [ObservableProperty] private string _lockBadgeText = "Read";
    [ObservableProperty] private bool _lockedHintVisible;
    [ObservableProperty] private bool _hasUnappliedChanges;
    [ObservableProperty] private bool _isApplyingChanges;

    // --- Hero Battery Card & Multi-Mouse Switcher ---
    public ObservableCollection<BatterySegmentItemViewModel> BatterySegments { get; } = [];
    public ObservableCollection<MouseSwitchItemViewModel> AvailableMice { get; } = [];

    [ObservableProperty] private bool _hasMultipleMice;
    [ObservableProperty] private bool _hasBatterySupport = true;
    [ObservableProperty] private bool _supportsHardwareSettings = true;
    [ObservableProperty] private string _batteryPercentHeroText = "—";
    [ObservableProperty] private Brush _batteryPercentHeroBrush = BrushBright;
    [ObservableProperty] private string _batteryPrimarySubline = "Polling device...";
    [ObservableProperty] private string _batteryChannelBadge = "2.4 GHz";
    [ObservableProperty] private string _batteryDrainRateText = "Collecting discharge history";
    [ObservableProperty] private bool _isBatteryHistoryExpanded;
    [ObservableProperty] private bool _hasSparklineData;
    [ObservableProperty] private PointCollection _batterySparklinePoints = [];
    [ObservableProperty] private string _vendorCoexistenceText = "Compatibility: no software conflicts";
    [ObservableProperty] private bool _hasVendorSoftwareDetected;

    // --- Live Raw Input Polling Rate (Hz) Tester ---
    [ObservableProperty] private bool _isPollingTestActive;
    [ObservableProperty] private string _measuredPollingHzText = "— Hz";
    [ObservableProperty] private string _peakPollingHzText = "move mouse";

    // --- Compact Header & Live Telemetry ---
    [ObservableProperty] private string _shortDeviceName = "Mouse";
    [ObservableProperty] private string _deviceName = "No mouse detected";
    [ObservableProperty] private string _deviceKeyDisplay = "—";
    [ObservableProperty] private string _deviceInterfaceDisplay = "—";
    [ObservableProperty] private string _protocolName = "No driver";
    [ObservableProperty] private string _statusTooltip = "Offline";
    [ObservableProperty] private Brush _statusDotBrush = CreateBrush(107, 107, 107);
    [ObservableProperty] private string _headerBatteryLine = "—";
    [ObservableProperty] private string _transportLabel = "—";
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private bool _isSleeping;
    [ObservableProperty] private bool _hasPendingSync;

    [ObservableProperty] private string _currentDpiDisplay = "800";
    [ObservableProperty] private string _dpiStageMetaText = "Stage 1 of 4 · step 50";
    [ObservableProperty] private string _pollingLatencyText = "1000 Hz · 1 ms";
    [ObservableProperty] private string _configOriginText = "Read-only";
    [ObservableProperty] private string _syncFooterText = "Read-only";

    // --- Main Screen & Settings: Profiles, DPI, Polling, Sensor ---
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<DpiStageItemViewModel> DpiStages { get; } = [];
    public ObservableCollection<PollingRateItemViewModel> PollingRates { get; } = [];

    public IReadOnlyList<string> StageColorSwatches { get; } =
        ["#FFEF4444", "#FFF97316", "#FFEAB308", "#FF22C55E", "#FF06B6D4", "#FF38BDF8", "#FFA855F7", "#FFEC4899"];

    [ObservableProperty] private bool _independentXY;
    [ObservableProperty] private int _selectedDpiValue = 800;
    [ObservableProperty] private int _selectedDpiYValue = 800;
    [ObservableProperty] private int _minDpi = 50;
    [ObservableProperty] private int _maxDpi = 26000;
    [ObservableProperty] private int _quickMinDpi = 400;
    [ObservableProperty] private int _quickMaxDpi = 6400;
    [ObservableProperty] private int _dpiStep = 50;
    [ObservableProperty] private string _activeProfileNameEdit = string.Empty;
    [ObservableProperty] private bool _canDeleteActiveProfile;

    [ObservableProperty] private double _selectedLodMm = 1.0;
    [ObservableProperty] private bool _supportsLod07 = true;
    [ObservableProperty] private int _debounceMs = 2;
    [ObservableProperty] private int _angleTuningDegrees;
    [ObservableProperty] private bool _motionSync;
    [ObservableProperty] private bool _rippleControl;
    [ObservableProperty] private bool _angleSnapping;
    [ObservableProperty] private bool _highSpeedMode;
    [ObservableProperty] private bool _supportsHighSpeedMode = true;
    [ObservableProperty] private int _sleepTimeoutMinutes = 5;

    // --- Settings -> OS & Buttons ---
    [ObservableProperty] private int _windowsPointerSpeed = 10;
    [ObservableProperty] private string _windowsPointerSpeedLabel = "10/20 (1:1)";
    [ObservableProperty] private bool _windowsEnhancePointerPrecision;
    [ObservableProperty] private int _windowsWheelScrollLines = 3;
    [ObservableProperty] private int _windowsDoubleClickMs = 500;

    public ObservableCollection<ButtonMappingItemViewModel> ButtonMappings { get; } = [];

    public IReadOnlyList<string> AvailableButtonActions { get; } =
    [
        "LMB (Primary)",
        "RMB (Context menu)",
        "MMB (Middle click)",
        "Forward (M5)",
        "Back (M4)",
        "DPI Cycle +",
        "DPI Cycle −",
        "Sniper DPI",
        "Switch Profile",
        "Media: Play/Pause",
        "Copy",
        "Paste",
        "Disabled"
    ];

    // --- Settings -> Automation & Preferences ---
    public ObservableCollection<ProcessRuleItemViewModel> ProcessRules { get; } = [];
    public ObservableCollection<string> RunningProcesses { get; } = [];
    public ObservableCollection<HotkeyBinding> Hotkeys { get; } = [];

    [ObservableProperty] private bool _autoSwitchEnabled;
    [ObservableProperty] private string _activeForegroundProcess = "explorer.exe";
    [ObservableProperty] private string _defaultDesktopProfileId = "eco";
    [ObservableProperty] private string _newRuleProcessName = string.Empty;
    [ObservableProperty] private string _newRuleProfileId = "gaming";
    [ObservableProperty] private string _hotkeyStatusText = string.Empty;

    [ObservableProperty] private bool _autoStartEnabled;
    [ObservableProperty] private bool _lowBatteryAlertEnabled = true;
    [ObservableProperty] private int _lowBatteryThresholdPercent = 15;
    [ObservableProperty] private bool _fullBatteryAlertEnabled = true;
    [ObservableProperty] private bool _unlockEditingOnStartup;
    [ObservableProperty] private TrayIconStyle _selectedTrayIconStyle = TrayIconStyle.TaskbarPillIconAndPercent;
    [ObservableProperty] private FlyoutBackdropStyle _selectedBackdropStyle = FlyoutBackdropStyle.Acrylic;

    // --- Settings -> HID Scanner ---
    public ObservableCollection<HidDeviceItemViewModel> ScannedDevices { get; } = [];
    public ObservableCollection<HidPacketLogEntry> PacketLogs { get; } = [];

    [ObservableProperty] private bool _showOnlyMiceAndVendor = true;
    [ObservableProperty] private HidDeviceItemViewModel? _selectedScannedDevice;
    [ObservableProperty] private bool _enableVirtualMouse;
    [ObservableProperty] private int _virtualBatteryPercent = 84;
    [ObservableProperty] private bool _virtualCharging;
    [ObservableProperty] private bool _virtualSleeping;

    [ObservableProperty] private bool _useFeatureReport = true;
    [ObservableProperty] private string _rawReportIdHex = "08";
    [ObservableProperty] private string _rawPayloadHex = "04 00 00 00 00";
    [ObservableProperty] private ChecksumAlgorithm _selectedChecksum = ChecksumAlgorithm.CompX55;
    [ObservableProperty] private bool _readResponseAfterSend = true;
    [ObservableProperty] private string _scannerStatusMessage = string.Empty;

    public IReadOnlyList<ChecksumAlgorithm> ChecksumAlgorithms { get; } = Enum.GetValues<ChecksumAlgorithm>();
    public ObservableCollection<TrayStyleChoice> TrayStyleChoices { get; } = [];
    public ObservableCollection<BackdropStyleChoice> BackdropStyleChoices { get; } = [];
    public IReadOnlyList<int> SleepTimeoutChoices { get; } = [1, 3, 5, 10, 15, 30];

    private void RebuildLocalizedChoiceLists()
    {
        var savedTray = SelectedTrayIconStyle;
        TrayStyleChoices.Clear();
        TrayStyleChoices.Add(new(TrayIconStyle.TaskbarPillIconAndPercent, P("Панель задач: мышь + %", "Taskbar: mouse + %")));
        TrayStyleChoices.Add(new(TrayIconStyle.TaskbarPillNameAndPercent, P("Панель задач: имя + %", "Taskbar: name + %")));
        TrayStyleChoices.Add(new(TrayIconStyle.TrayLargePercent, P("Трей: крупные цифры %", "Tray: large percent")));
        TrayStyleChoices.Add(new(TrayIconStyle.TrayMouseBatteryFill, P("Трей: заливка силуэта мыши", "Tray: battery-filled mouse")));
        TrayStyleChoices.Add(new(TrayIconStyle.TrayRingBadge, P("Трей: кольцо заряда", "Tray: progress ring")));
        SelectedTrayIconStyle = savedTray;

        var savedBackdrop = SelectedBackdropStyle;
        BackdropStyleChoices.Clear();
        BackdropStyleChoices.Add(new(FlyoutBackdropStyle.Acrylic, P("Глубокий графит", "Deep Graphite")));
        BackdropStyleChoices.Add(new(FlyoutBackdropStyle.Mica, P("Мягкий графит", "Soft Graphite")));
        SelectedBackdropStyle = savedBackdrop;
    }

    [RelayCommand]
    private async Task ToggleLanguageAsync()
    {
        var next = AppLocalization.IsRussian ? AppLanguage.English : AppLanguage.Russian;
        await ApplyLanguageAsync(next);
    }

    [RelayCommand]
    private async Task SelectLanguageAsync(string? langCode)
    {
        var next = string.Equals(langCode, "ru", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Russian
            : AppLanguage.English;
        await ApplyLanguageAsync(next);
    }

    private async Task ApplyLanguageAsync(AppLanguage language)
    {
        AppLocalization.SetLanguage(language);
        UiStrings.Instance.Refresh();
        LockBadgeText = IsWriteUnlocked ? Loc.WriteBadge : Loc.ReadBadge;
        if (!IsPollingTestActive)
        {
            MeasuredPollingHzText = P("— Гц", "— Hz");
            PeakPollingHzText = P("двигайте мышью", "move mouse");
        }
        RebuildLocalizedChoiceLists();
        RefreshAllFromCoordinator();
        if (IsSettingsOpen || ScannedDevices.Count > 0)
            RefreshScanner();

        await _coordinator.UpdateConfigAsync(c => c.Language = language);
    }

    [RelayCommand]
    private void OpenFirstRunSetup()
    {
        IsFirstRunSetupVisible = true;
    }

    [RelayCommand]
    private async Task CompleteFirstRunSetupAsync()
    {
        IsFirstRunSetupVisible = false;
        await _coordinator.UpdateConfigAsync(c => c.IsFirstRunSetupCompleted = true);
    }

    [RelayCommand]
    private async Task OpenHidScannerFromSetupAsync()
    {
        IsFirstRunSetupVisible = false;
        IsSettingsOpen = true;
        SettingsTabIndex = 3;
        RefreshScanner();
        await _coordinator.UpdateConfigAsync(c => c.IsFirstRunSetupCompleted = true);
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value)
        {
            if (ScannedDevices.Count == 0)
                RefreshScanner();
            _ = RefreshRunningProcessesAsync();
        }
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ToggleBatteryHistory() => IsBatteryHistoryExpanded = !IsBatteryHistoryExpanded;

    [RelayCommand]
    private void UnlockNow()
    {
        IsWriteUnlocked = true;
        LockedHintVisible = false;
    }

    [RelayCommand]
    private async Task SelectMouseAsync(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return;
        await _coordinator.SelectDeviceAsync(devicePath);
    }

    [RelayCommand]
    private void TogglePollingTest()
    {
        if (IsPollingTestActive)
        {
            StopPollingTest();
        }
        else
        {
            MeasuredPollingHzText = P("0 Гц", "0 Hz");
            PeakPollingHzText = P("двигайте мышью...", "move mouse...");
            _pollingTester.Start();
            IsPollingTestActive = _pollingTester.IsRunning;
        }
    }

    public void StopPollingTest()
    {
        if (!IsPollingTestActive) return;
        _pollingTester.Stop();
        IsPollingTestActive = false;
    }

    private void OnRawPollingRateUpdated(int currentHz, int peakHz)
    {
        RunOnUi(() =>
        {
            MeasuredPollingHzText = P($"{currentHz} Гц", $"{currentHz} Hz");
            PeakPollingHzText = P($"пик {peakHz} Гц", $"peak {peakHz} Hz");
        });
    }

    public void TriggerLockedHint()
    {
        if (IsWriteUnlocked || !SupportsHardwareSettings) return;
        LockedHintVisible = true;
        _lockedHintCts?.Cancel();
        _lockedHintCts?.Dispose();
        _lockedHintCts = new CancellationTokenSource();
        var ct = _lockedHintCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2600, ct);
                RunOnUi(() => LockedHintVisible = false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void NudgeDpiByWheel(int wheelDelta)
    {
        if (!SupportsHardwareSettings) return;
        if (!IsWriteUnlocked)
        {
            TriggerLockedHint();
            return;
        }
        int delta = wheelDelta > 0 ? DpiStep : -DpiStep;
        SelectedDpiValue = Math.Clamp(((SelectedDpiValue + delta) / DpiStep) * DpiStep, MinDpi, MaxDpi);
    }

    partial void OnIsWriteUnlockedChanged(bool value)
    {
        LockBadgeText = value ? Loc.WriteBadge : Loc.ReadBadge;
        if (value) LockedHintVisible = false;
        _coordinator.SetWriteUnlocked(value);
        if (value)
        {
            InitializeStagingFromCurrent();
        }
        else
        {
            _stagedProfile = null;
            _stagedProfileId = null;
            _baselineHash = null;
            HasUnappliedChanges = false;
            RefreshProfileAndConfigBindings();
        }
    }

    private void InitializeStagingFromCurrent()
    {
        var current = _coordinator.GetActiveProfile();
        _stagedProfile = current.Clone();
        _stagedProfileId = current.Id;
        _baselineHash = $"{current.Id}:{current.ComputeSyncHash()}";
        HasUnappliedChanges = false;
    }

    private MouseProfile GetWorkingProfile()
    {
        if (IsWriteUnlocked && _stagedProfile is not null)
            return _stagedProfile;
        return _coordinator.GetActiveProfile();
    }

    private void MarkStagedDirty()
    {
        if (!IsWriteUnlocked || _stagedProfile is null) return;
        string nowHash = $"{_stagedProfileId}:{_stagedProfile.ComputeSyncHash()}";
        HasUnappliedChanges = !string.Equals(nowHash, _baselineHash, StringComparison.Ordinal);
        UpdateProfileTelemetryLabels(_stagedProfile);
    }

    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        if (!IsWriteUnlocked || _stagedProfile is null || string.IsNullOrWhiteSpace(_stagedProfileId))
            return;

        IsApplyingChanges = true;
        try
        {
            var draft = _stagedProfile.Clone();
            var targetId = _stagedProfileId;

            if (_coordinator.Config.ActiveProfileId != targetId)
            {
                await _coordinator.UpdateConfigAsync(c => c.ActiveProfileId = targetId);
            }

            await _coordinator.UpdateActiveProfileAsync(p =>
            {
                p.ActiveDpiStageIndex = draft.ActiveDpiStageIndex;
                p.IndependentXY = draft.IndependentXY;
                p.DpiStages = draft.DpiStages.Select(s => s.Clone()).ToList();
                p.PollingRateHz = draft.PollingRateHz;
                p.LodMm = draft.LodMm;
                p.DebounceMs = draft.DebounceMs;
                p.AngleTuningDegrees = draft.AngleTuningDegrees;
                p.MotionSync = draft.MotionSync;
                p.RippleControl = draft.RippleControl;
                p.AngleSnapping = draft.AngleSnapping;
                p.HighSpeedMode = draft.HighSpeedMode;
                p.SleepTimeoutMinutes = draft.SleepTimeoutMinutes;
            }, syncHardware: true);

            InitializeStagingFromCurrent();
        }
        finally
        {
            IsApplyingChanges = false;
        }
    }

    [RelayCommand]
    private async Task RevertChangesAsync()
    {
        _stagedProfile = null;
        _stagedProfileId = null;
        HasUnappliedChanges = false;

        // Re-read actual hardware settings from mouse without writing anything
        await _coordinator.ReadConfigFromActiveDeviceAsync();
        RefreshProfileAndConfigBindings();

        if (IsWriteUnlocked)
        {
            InitializeStagingFromCurrent();
        }
    }

    public void OnFlyoutOpened()
    {
        RefreshWindowsSettings();
        _ = _coordinator.PollBatteryAsync();
    }

    public void OnFlyoutClosed()
    {
        StopPollingTest();
    }

    private void OnCoordinatorStateChanged(MouseRuntimeState state) =>
        RunOnUi(() => ApplyRuntimeStateToUi(state));

    private void OnCoordinatorConfigChanged(AppConfig _) =>
        RunOnUi(RefreshProfileAndConfigBindings);

    private void OnPacketLogged(HidPacketLogEntry entry)
    {
        RunOnUi(() =>
        {
            PacketLogs.Add(entry);
            while (PacketLogs.Count > 250)
                PacketLogs.RemoveAt(0);
        });
    }

    private void RefreshAllFromCoordinator()
    {
        ApplyRuntimeStateToUi(_coordinator.State);
        RefreshProfileAndConfigBindings();
    }

    private void ApplyRuntimeStateToUi(MouseRuntimeState s)
    {
        var active = _coordinator.GetActiveProfile();

        DeviceName = s.DeviceName;
        ShortDeviceName = FormatCleanDeviceName(s.DeviceName);
        DeviceKeyDisplay = string.IsNullOrWhiteSpace(s.DeviceKey) ? "—" : s.DeviceKey;
        DeviceInterfaceDisplay = string.IsNullOrWhiteSpace(s.DeviceInterfaceInfo) ? "—" : s.DeviceInterfaceInfo;
        ProtocolName = s.ProtocolName;
        IsCharging = s.IsCharging;
        IsSleeping = s.Status == MouseConnectionStatus.Sleeping;
        HasPendingSync = s.HasPendingSync;
        ActiveForegroundProcess = string.IsNullOrWhiteSpace(s.ActiveForegroundProcess) ? "explorer.exe" : s.ActiveForegroundProcess;

        TransportLabel = s.Transport switch
        {
            MouseTransportKind.Wireless24G => "2.4 GHz",
            MouseTransportKind.WiredUsb => "USB",
            MouseTransportKind.Bluetooth => "Bluetooth",
            MouseTransportKind.Virtual => "Demo",
            _ => P("Нет связи", "Offline")
        };

        // Tiny 6px status dot ONLY: toned semantic tokens (#4CC38A active, #D99B43 sleep, #E5484D low/error)
        (StatusTooltip, StatusDotBrush) = s.Status switch
        {
            MouseConnectionStatus.Charging => (P("Зарядка по USB", "Charging via USB"), BrushGood),
            MouseConnectionStatus.Active when (s.BatteryPercent ?? 100) <= 15 => (P("Активна · низкий заряд", "Active · low battery"), BrushDanger),
            MouseConnectionStatus.Active => (P($"Активна · {TransportLabel}", $"Active · {TransportLabel}"), BrushGood),
            MouseConnectionStatus.Sleeping => (P("Сон (кэш заряда)", "Sleeping (cached battery)"), BrushWarn),
            MouseConnectionStatus.Connecting => (P("Подключение...", "Connecting"), BrushWarn),
            _ => (P("Устройство отключено", "Device disconnected"), CreateBrush(133, 133, 139))
        };

        var caps = _coordinator.ActiveCapabilities;
        MinDpi = caps.MinDpi;
        MaxDpi = caps.MaxDpi;
        DpiStep = caps.DpiStep;
        SupportsLod07 = caps.SupportedLodMm.Any(l => l <= 0.8);
        SupportsHighSpeedMode = caps.SupportsHighSpeedMode;
        HasBatterySupport = caps.SupportsBatteryRead || s.BatteryPercent.HasValue;
        SupportsHardwareSettings = caps.SupportsHardwareSettings;

        foreach (var pr in PollingRates)
            pr.IsSupported = caps.SupportedPollingRatesHz.Contains(pr.RateHz);

        // Vendor coexistence notice
        HasVendorSoftwareDetected = !string.IsNullOrWhiteSpace(s.DetectedVendorSoftware);
        VendorCoexistenceText = HasVendorSoftwareDetected
            ? P($"Работает вместе с {s.DetectedVendorSoftware} (щадящий режим)", $"Running alongside {s.DetectedVendorSoftware} (gentle mode)")
            : P("Совместимость: конфликтов с ПО нет", "Compatibility: no software conflicts");

        // Multi-mouse switcher list
        AvailableMice.Clear();
        foreach (var m in s.AvailableMice)
        {
            AvailableMice.Add(new MouseSwitchItemViewModel
            {
                DevicePath = m.DevicePath,
                DeviceKey = m.DeviceKey,
                DisplayName = FormatCleanDeviceName(m.DisplayName),
                ProtocolName = m.ProtocolName,
                TransportBadge = m.Transport switch
                {
                    MouseTransportKind.Bluetooth => "BT",
                    MouseTransportKind.WiredUsb => "USB",
                    MouseTransportKind.Virtual => "Demo",
                    _ => "2.4G"
                },
                IsSelected = m.IsSelected
            });
        }
        HasMultipleMice = AvailableMice.Count > 1;

        UpdateBatteryHeroCard(s, active);
        UpdateProfileTelemetryLabels(active);

        ConfigOriginText = s.ConfigOrigin switch
        {
            HardwareConfigOrigin.ReadFromDevice => P("Считано с мыши", "Read from device"),
            HardwareConfigOrigin.AppliedByUser => P("Синхронизировано", "Synced"),
            _ => P("Только чтение", "Read-only")
        };

        SyncFooterText = s.HasPendingSync
            ? P("Ожидание пробуждения", "Waiting for wake")
            : s.ConfigOrigin switch
            {
                HardwareConfigOrigin.ReadFromDevice => P("Считано с мыши", "Read from mouse"),
                HardwareConfigOrigin.AppliedByUser when s.LastSyncUtc is DateTimeOffset ts => P($"Применено в {ts.ToLocalTime():HH:mm:ss}", $"Applied at {ts.ToLocalTime():HH:mm:ss}"),
                _ => P("Только чтение", "Read-only")
            };
    }

    private void UpdateBatteryHeroCard(MouseRuntimeState s, MouseProfile active)
    {
        if (s.Status == MouseConnectionStatus.Disconnected)
        {
            HeaderBatteryLine = P("Нет связи", "Offline");
            BatteryPercentHeroText = "—";
            BatteryPercentHeroBrush = CreateBrush(133, 133, 139);
            BatteryPrimarySubline = P("Подключите мышь или ресивер", "Connect a mouse or receiver");
            BatteryChannelBadge = P("Нет связи", "Offline");
            BatteryDrainRateText = P("Нет данных о заряде", "No battery data");
            UpdateBatterySegments(0, BrushSegmentEmpty);
            UpdateSparkline(s.BatteryHistory);
            return;
        }

        if (s.BatteryPercent is not int pct)
        {
            HeaderBatteryLine = TransportLabel;
            BatteryPercentHeroText = "USB";
            BatteryPercentHeroBrush = BrushBright;
            BatteryPrimarySubline = P($"{s.ProtocolName} · питание по кабелю", $"{s.ProtocolName} · wired power");
            BatteryChannelBadge = TransportLabel;
            BatteryDrainRateText = P("Мониторинг аккумулятора не требуется", "No battery monitoring required");
            UpdateBatterySegments(100, BrushGood);
            HasSparklineData = false;
            return;
        }

        int clamped = Math.Clamp(pct, 0, 100);
        string volt = s.VoltageMv is int mv and > 2500
            ? string.Create(CultureInfo.InvariantCulture, $"{mv / 1000.0:0.00} ") + P("В", "V")
            : string.Empty;

        Brush accentBrush = s.IsCharging
            ? BrushGood
            : clamped <= 15
                ? BrushDanger
                : clamped <= 25
                    ? BrushWarn
                    : BrushBright;

        Brush barBrush = s.IsCharging
            ? BrushGood
            : clamped <= 15
                ? BrushDanger
                : clamped <= 30
                    ? BrushWarn
                    : BrushGood;

        BatteryPercentHeroText = $"{clamped}%";
        BatteryPercentHeroBrush = accentBrush;

        string ago = s.LastBatteryUpdateUtc is DateTimeOffset ts
            ? FormatRelativeAgo(DateTimeOffset.UtcNow - ts)
            : "—";

        if (s.IsCharging)
        {
            if (clamped >= 100)
            {
                HeaderBatteryLine = string.IsNullOrEmpty(volt) ? P("100% · Заряжена", "100% · Charged") : P($"100% · {volt} · Заряжена", $"100% · {volt} · Charged");
                BatteryPrimarySubline = string.IsNullOrEmpty(volt) ? P("Полностью заряжена", "Fully charged") : P($"Полностью заряжена · {volt}", $"Fully charged · {volt}");
                BatteryChannelBadge = "USB · 100%";
                BatteryDrainRateText = P($"Зарядка завершена · {ago}", $"Charge complete · {ago}");
            }
            else
            {
                var chargeEst = BatteryHistoryService.EstimateCharging(clamped, s.BatteryHistory);
                string eta = BatteryHistoryService.FormatDuration(chargeEst.RemainingTime);

                HeaderBatteryLine = string.IsNullOrEmpty(volt)
                    ? P($"{clamped}% · {eta} до 100%", $"{clamped}% · {eta} to full")
                    : P($"{clamped}% · {volt} · {eta} до 100%", $"{clamped}% · {volt} · {eta} to full");
                BatteryPrimarySubline = string.IsNullOrEmpty(volt)
                    ? P($"До 100% {eta}", $"Full in {eta}")
                    : P($"До 100% {eta} · {volt}", $"Full in {eta} · {volt}");
                BatteryChannelBadge = P("USB · Зарядка", "USB · Charging");

                BatteryDrainRateText = chargeEst.MeasuredPercentPerMinute is double cRate
                    ? string.Create(CultureInfo.InvariantCulture, $"{P("Заряд", "Charging")}: +{cRate:0.0} %/{P("мин", "min")} · {ago}")
                    : P($"Оценка CC-CV (калибровка) · {ago}", $"CC-CV estimate (calibrating) · {ago}");
            }
        }
        else
        {
            var disEst = BatteryHistoryService.EstimateDischarge(clamped, active, s.BatteryHistory, s.VoltageMv);
            string hrs = BatteryHistoryService.FormatDuration(disEst.RemainingTime);
            HeaderBatteryLine = string.IsNullOrEmpty(volt) ? $"{clamped}% · {hrs}" : $"{clamped}% · {volt} · {hrs}";
            BatteryPrimarySubline = string.IsNullOrEmpty(volt)
                ? P($"осталось {hrs}", $"{hrs} remaining")
                : P($"{volt} · осталось {hrs}", $"{volt} · {hrs} left");

            string rateSuffix = SupportsHardwareSettings
                ? $" · {(active.PollingRateHz >= 1000 ? $"{active.PollingRateHz / 1000}K" : active.PollingRateHz.ToString())}"
                : string.Empty;
            BatteryChannelBadge = s.Status == MouseConnectionStatus.Sleeping
                ? P($"{TransportLabel} · Сон", $"{TransportLabel} · Sleep")
                : $"{TransportLabel}{rateSuffix}";

            BatteryDrainRateText = disEst.MeasuredPercentPerHour is double drain
                ? string.Create(CultureInfo.InvariantCulture, $"{P("Расход", "Drain")}: {drain:0.0} %/{P("ч", "h")} · {ago}")
                : P($"Модель {active.PollingRateHz} Гц · точек: {s.BatteryHistory.Count}", $"{active.PollingRateHz} Hz model · samples: {s.BatteryHistory.Count}");
        }

        UpdateBatterySegments(clamped, barBrush);
        UpdateSparkline(s.BatteryHistory);
    }

    private void UpdateBatterySegments(int percent, Brush fillBrush)
    {
        int filledCount = percent <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(percent / 10.0), 1, 10);
        for (int i = 0; i < BatterySegments.Count; i++)
        {
            bool filled = i < filledCount;
            BatterySegments[i].IsFilled = filled;
            BatterySegments[i].FillBrush = filled ? fillBrush : BrushSegmentEmpty;
        }
    }

    private void UpdateSparkline(IReadOnlyList<BatteryHistoryPoint> history)
    {
        if (history is null || history.Count < 2)
        {
            HasSparklineData = false;
            BatterySparklinePoints = [];
            return;
        }

        const double chartW = 304.0;
        const double chartH = 34.0;
        const double padY = 3.0;

        var pts = new PointCollection();
        int minPct = Math.Max(0, history.Min(h => h.Percent) - 5);
        int maxPct = Math.Min(100, history.Max(h => h.Percent) + 5);
        double spanPct = Math.Max(10.0, maxPct - minPct);

        for (int i = 0; i < history.Count; i++)
        {
            double x = (i / (double)(history.Count - 1)) * chartW;
            double norm = (history[i].Percent - minPct) / spanPct;
            double y = (chartH - padY) - (norm * (chartH - padY * 2));
            pts.Add(new Point(Math.Round(x, 1), Math.Round(y, 1)));
        }
        pts.Freeze();
        BatterySparklinePoints = pts;
        HasSparklineData = true;
    }

    private static string FormatRelativeAgo(TimeSpan age)
    {
        if (age.TotalSeconds < 45) return P("только что", "just now");
        if (age.TotalMinutes < 60) return P($"{(int)age.TotalMinutes} мин назад", $"{(int)age.TotalMinutes}m ago");
        return P($"{(int)age.TotalHours} ч назад", $"{(int)age.TotalHours}h ago");
    }

    private void UpdateProfileTelemetryLabels(MouseProfile active)
    {
        CurrentDpiDisplay = active.IndependentXY
            ? $"{active.CurrentDpi}×{active.CurrentDpiY}"
            : active.CurrentDpi.ToString();

        int stageNum = Math.Clamp(active.ActiveDpiStageIndex + 1, 1, Math.Max(1, active.DpiStages.Count));
        DpiStageMetaText = P(
            $"Ступень {stageNum} из {active.DpiStages.Count} · шаг {DpiStep}",
            $"Stage {stageNum} of {active.DpiStages.Count} · step {DpiStep}");

        string lat = active.PollingRateHz >= 2000
            ? string.Create(CultureInfo.InvariantCulture, $"{active.PollingLatencyMs:0.##} ") + P("мс", "ms")
            : string.Create(CultureInfo.InvariantCulture, $"{active.PollingLatencyMs:0.#} ") + P("мс", "ms");
        PollingLatencyText = P($"{active.PollingRateHz} Гц · {lat}", $"{active.PollingRateHz} Hz · {lat}");

        UpdateBatteryHeroCard(_coordinator.State, active);
    }

    private void RefreshProfileAndConfigBindings()
    {
        _isSyncingFromModel = true;
        try
        {
            var cfg = _coordinator.Config;
            var active = GetWorkingProfile();
            var activeId = IsWriteUnlocked && !string.IsNullOrWhiteSpace(_stagedProfileId)
                ? _stagedProfileId!
                : active.Id;

            Profiles.Clear();
            foreach (var p in cfg.Profiles)
            {
                Profiles.Add(new ProfileItemViewModel
                {
                    Id = p.Id,
                    Name = p.Name,
                    ShortName = FormatShortProfileName(p),
                    Subtitle = $"{(p.PollingRateHz >= 1000 ? $"{p.PollingRateHz / 1000}K" : p.PollingRateHz)}Hz · {p.CurrentDpi}",
                    IsActive = p.Id == activeId
                });
            }

            CanDeleteActiveProfile = cfg.Profiles.Count > 1;
            ActiveProfileNameEdit = active.Name;

            IndependentXY = active.IndependentXY;
            DpiStages.Clear();
            for (int i = 0; i < active.DpiStages.Count; i++)
            {
                DpiStages.Add(new DpiStageItemViewModel
                {
                    Index = i,
                    Dpi = active.DpiStages[i].Dpi,
                    DpiY = active.DpiStages[i].DpiY,
                    ColorHex = active.DpiStages[i].ColorHex,
                    IsSelected = i == active.ActiveDpiStageIndex
                });
            }

            QuickMinDpi = Math.Min(400, active.CurrentDpi);
            QuickMaxDpi = Math.Max(6400, active.CurrentDpi);
            SelectedDpiValue = active.CurrentDpi;
            SelectedDpiYValue = active.CurrentDpiY;

            foreach (var pr in PollingRates)
                pr.IsSelected = pr.RateHz == active.PollingRateHz;

            SelectedLodMm = active.LodMm;
            DebounceMs = active.DebounceMs;
            AngleTuningDegrees = active.AngleTuningDegrees;
            MotionSync = active.MotionSync;
            RippleControl = active.RippleControl;
            AngleSnapping = active.AngleSnapping;
            HighSpeedMode = active.HighSpeedMode;
            SleepTimeoutMinutes = active.SleepTimeoutMinutes;

            RebuildButtonMappings(active);
            UpdateProfileTelemetryLabels(active);

            AutoSwitchEnabled = cfg.AutoSwitchEnabled;
            PinWindowOpen = cfg.PinWindowOpen;
            DefaultDesktopProfileId = cfg.DefaultDesktopProfileId;
            if (!cfg.Profiles.Any(p => p.Id == NewRuleProfileId))
                NewRuleProfileId = cfg.Profiles.Last().Id;

            ProcessRules.Clear();
            foreach (var rule in cfg.ProcessRules)
            {
                var pName = cfg.Profiles.FirstOrDefault(p => p.Id == rule.ProfileId)?.Name ?? rule.ProfileId;
                ProcessRules.Add(new ProcessRuleItemViewModel
                {
                    Id = rule.Id,
                    ProcessName = rule.ProcessName,
                    ProfileId = rule.ProfileId,
                    ProfileName = pName,
                    Enabled = rule.Enabled
                });
            }

            Hotkeys.Clear();
            foreach (var hk in cfg.Hotkeys)
            {
                hk.Label = hk.Action switch
                {
                    "CycleProfile" => P("Следующий профиль", "Next profile"),
                    "ToggleEcoGaming" => P("Переключить Эко / Игровой", "Toggle Eco / Gaming"),
                    _ => hk.Label
                };
                Hotkeys.Add(hk);
            }

            AutoStartEnabled = AutostartService.IsEnabled();
            LowBatteryAlertEnabled = cfg.LowBatteryAlertEnabled;
            LowBatteryThresholdPercent = cfg.LowBatteryThresholdPercent;
            FullBatteryAlertEnabled = cfg.FullBatteryAlertEnabled;
            UnlockEditingOnStartup = cfg.UnlockEditingOnStartup;
            SelectedTrayIconStyle = cfg.TrayIconStyle;
            SelectedBackdropStyle = cfg.BackdropStyle;
            EnableVirtualMouse = cfg.EnableVirtualMouse;
            VirtualBatteryPercent = _coordinator.VirtualMouse.SimulatedBatteryPercent;
            VirtualCharging = _coordinator.VirtualMouse.SimulatedCharging;
            VirtualSleeping = _coordinator.VirtualMouse.SimulatedSleeping;
        }
        finally
        {
            _isSyncingFromModel = false;
        }
    }

    private void RebuildButtonMappings(MouseProfile profile)
    {
        var map = profile.ButtonMappings ?? MouseProfile.CreateDefaultButtonMappings();
        (string Key, string Label)[] defs =
        [
            ("Left", P("ЛКМ", "LMB")),
            ("Right", P("ПКМ", "RMB")),
            ("Middle", P("Колесо (СКМ)", "Wheel (MMB)")),
            ("Forward", P("Вперед (M5)", "Forward (M5)")),
            ("Back", P("Назад (M4)", "Back (M4)")),
            ("Dpi", P("Кнопка DPI", "DPI Button"))
        ];

        ButtonMappings.Clear();
        foreach (var (key, label) in defs)
        {
            string rawAct = map.TryGetValue(key, out var act) && !string.IsNullOrWhiteSpace(act) ? act : AvailableButtonActions[0];
            string normAct = rawAct switch
            {
                "ЛКМ (Основная)" => "LMB (Primary)",
                "ПКМ (Меню)" => "RMB (Context menu)",
                "СКМ (Колесо)" => "MMB (Middle click)",
                "Вперед (Браузер / M5)" or "Вперед (M5)" => "Forward (M5)",
                "Назад (Браузер / M4)" or "Назад (M4)" => "Back (M4)",
                "Цикл DPI +" => "DPI Cycle +",
                "Цикл DPI −" => "DPI Cycle −",
                "Снайпер DPI" => "Sniper DPI",
                "Сменить профиль" => "Switch Profile",
                "Медиа: Пауза" => "Media: Play/Pause",
                "Копировать" => "Copy",
                "Вставить" => "Paste",
                "Отключена" => "Disabled",
                _ => AvailableButtonActions.Contains(rawAct) ? rawAct : AvailableButtonActions[0]
            };

            var item = new ButtonMappingItemViewModel
            {
                Key = key,
                ButtonLabel = label,
                SelectedAction = normAct
            };
            item.PropertyChanged += (_, args) =>
            {
                if (_isSyncingFromModel || args.PropertyName != nameof(ButtonMappingItemViewModel.SelectedAction))
                    return;
                _ = _coordinator.UpdateActiveProfileAsync(p =>
                {
                    p.ButtonMappings ??= MouseProfile.CreateDefaultButtonMappings();
                    p.ButtonMappings[item.Key] = item.SelectedAction;
                }, syncHardware: false);
            };
            ButtonMappings.Add(item);
        }
    }

    // --- Windows OS Mouse Settings (Strictly Read-Only on Startup!) ---
    [RelayCommand]
    private void RefreshWindowsSettings()
    {
        _isLoadingWinSettings = true;
        try
        {
            var win = WindowsMouseSettingsService.ReadCurrent();
            WindowsPointerSpeed = win.PointerSpeed;
            WindowsEnhancePointerPrecision = win.EnhancePointerPrecision;
            WindowsWheelScrollLines = win.WheelScrollLines < 0 ? 12 : Math.Clamp(win.WheelScrollLines, 1, 20);
            WindowsDoubleClickMs = (int)win.DoubleClickTimeMs;
            UpdateWindowsSpeedLabel();
        }
        finally
        {
            _isLoadingWinSettings = false;
        }
    }

    private void UpdateWindowsSpeedLabel()
    {
        WindowsPointerSpeedLabel = WindowsPointerSpeed == 10
            ? "10 / 20 (1:1)"
            : $"{WindowsPointerSpeed} / 20";
    }

    partial void OnWindowsPointerSpeedChanged(int value)
    {
        UpdateWindowsSpeedLabel();
        if (_isLoadingWinSettings) return;
        WindowsMouseSettingsService.SetPointerSpeed(value);
    }

    partial void OnWindowsEnhancePointerPrecisionChanged(bool value)
    {
        if (_isLoadingWinSettings) return;
        WindowsMouseSettingsService.SetEnhancePointerPrecision(value);
    }

    partial void OnWindowsWheelScrollLinesChanged(int value)
    {
        if (_isLoadingWinSettings) return;
        WindowsMouseSettingsService.SetWheelScrollLines(value);
    }

    partial void OnWindowsDoubleClickMsChanged(int value)
    {
        if (_isLoadingWinSettings) return;
        WindowsMouseSettingsService.SetDoubleClickTimeMs((uint)Math.Clamp(value, 200, 900));
    }

    [RelayCommand]
    private async Task ResetButtonMappingsAsync()
    {
        await _coordinator.UpdateActiveProfileAsync(p =>
        {
            p.ButtonMappings = MouseProfile.CreateDefaultButtonMappings();
        }, syncHardware: false);
    }

    // --- Staged Profile Commands (Protected by Safety Lock) ---
    [RelayCommand]
    private void SelectProfile(string profileId)
    {
        if (!IsWriteUnlocked || string.IsNullOrWhiteSpace(profileId)) return;
        var found = _coordinator.Config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (found is null) return;

        _stagedProfile = found.Clone();
        _stagedProfileId = found.Id;
        RefreshProfileAndConfigBindings();
        MarkStagedDirty();
    }

    [RelayCommand]
    private async Task RenameActiveProfileAsync()
    {
        var clean = ActiveProfileNameEdit.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return;
        await _coordinator.UpdateActiveProfileAsync(p => p.Name = clean, syncHardware: false);
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        var clone = _coordinator.GetActiveProfile().Clone();
        clone.Id = $"custom-{Guid.NewGuid().ToString("N")[..6]}";
        clone.Name = $"Profile {_coordinator.Config.Profiles.Count + 1}";
        clone.IconKey = "Mouse";

        await _coordinator.UpdateConfigAsync(cfg => cfg.Profiles.Add(clone));
        await _coordinator.SelectProfileAsync(clone.Id, ProfileSwitchSource.Manual);
    }

    [RelayCommand]
    private async Task DeleteActiveProfileAsync()
    {
        if (_coordinator.Config.Profiles.Count <= 1) return;
        var currentId = _coordinator.Config.ActiveProfileId;

        await _coordinator.UpdateConfigAsync(cfg =>
        {
            cfg.Profiles.RemoveAll(p => p.Id == currentId);
            cfg.ProcessRules.RemoveAll(r => r.ProfileId == currentId);
            cfg.ActiveProfileId = cfg.Profiles[0].Id;
            if (cfg.DefaultDesktopProfileId == currentId)
                cfg.DefaultDesktopProfileId = cfg.Profiles[0].Id;
        });

        RefreshProfileAndConfigBindings();
    }

    [RelayCommand]
    private void ExportActiveProfile()
    {
        ModalDialogStateChanged?.Invoke(true);
        try
        {
            var active = _coordinator.GetActiveProfile();
            var dlg = new SaveFileDialog
            {
                Title = "Export Mouse Profile",
                Filter = "JSON Profile (*.json)|*.json",
                FileName = $"{active.Name.Replace('/', '_').Replace('\\', '_')}.json"
            };
            if (dlg.ShowDialog() == true)
            {
                var json = JsonSerializer.Serialize(active, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json);
            }
        }
        finally
        {
            ModalDialogStateChanged?.Invoke(false);
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        ModalDialogStateChanged?.Invoke(true);
        string? filePath = null;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Mouse Profile",
                Filter = "JSON Profile (*.json)|*.json",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
                filePath = dlg.FileName;
        }
        finally
        {
            ModalDialogStateChanged?.Invoke(false);
        }

        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var imported = JsonSerializer.Deserialize<MouseProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported is null || imported.DpiStages.Count == 0) return;

            imported.Id = $"imp-{Guid.NewGuid().ToString("N")[..6]}";
            if (string.IsNullOrWhiteSpace(imported.Name)) imported.Name = "Imported";

            await _coordinator.UpdateConfigAsync(cfg => cfg.Profiles.Add(imported));
            await _coordinator.SelectProfileAsync(imported.Id, ProfileSwitchSource.Manual);
        }
        catch { }
    }

    [RelayCommand]
    private async Task ReadFromDeviceAsync()
    {
        bool ok = await _coordinator.ReadConfigFromActiveDeviceAsync();
        SyncFooterText = ok ? "Read from mouse" : "Write-only / sleep";
    }

    // --- Staged DPI & Stage Controls ---
    [RelayCommand]
    private void SelectDpiStage(int index)
    {
        if (!IsWriteUnlocked || _stagedProfile is null || index < 0 || index >= _stagedProfile.DpiStages.Count)
            return;

        _stagedProfile.ActiveDpiStageIndex = index;
        _isSyncingFromModel = true;
        foreach (var st in DpiStages)
            st.IsSelected = st.Index == index;
        QuickMinDpi = Math.Min(400, _stagedProfile.CurrentDpi);
        QuickMaxDpi = Math.Max(6400, _stagedProfile.CurrentDpi);
        SelectedDpiValue = _stagedProfile.CurrentDpi;
        SelectedDpiYValue = _stagedProfile.CurrentDpiY;
        _isSyncingFromModel = false;

        MarkStagedDirty();
    }

    [RelayCommand]
    private void SetActiveStageColor(string hexColor)
    {
        if (!IsWriteUnlocked || _stagedProfile is null || string.IsNullOrWhiteSpace(hexColor)) return;
        int idx = Math.Clamp(_stagedProfile.ActiveDpiStageIndex, 0, _stagedProfile.DpiStages.Count - 1);
        _stagedProfile.DpiStages[idx].ColorHex = hexColor;
        MarkStagedDirty();
    }

    [RelayCommand]
    private void StepDpi(string deltaStr)
    {
        if (!IsWriteUnlocked || !int.TryParse(deltaStr, out int delta)) return;
        SelectedDpiValue = Math.Clamp(((SelectedDpiValue + delta) / DpiStep) * DpiStep, MinDpi, MaxDpi);
    }

    [RelayCommand]
    private void StepDpiY(string deltaStr)
    {
        if (!IsWriteUnlocked || !int.TryParse(deltaStr, out int delta)) return;
        SelectedDpiYValue = Math.Clamp(((SelectedDpiYValue + delta) / DpiStep) * DpiStep, MinDpi, MaxDpi);
    }

    [RelayCommand]
    private void AddDpiStage()
    {
        if (!IsWriteUnlocked || _stagedProfile is null) return;
        if (_stagedProfile.DpiStages.Count >= _coordinator.ActiveCapabilities.MaxDpiStages) return;
        int nextDpi = Math.Clamp((_stagedProfile.DpiStages.LastOrDefault()?.Dpi ?? 800) + 800, MinDpi, MaxDpi);
        string color = DefaultStageColors[_stagedProfile.DpiStages.Count % DefaultStageColors.Length];
        _stagedProfile.DpiStages.Add(new DpiStage { Dpi = nextDpi, DpiY = nextDpi, ColorHex = color });
        _stagedProfile.ActiveDpiStageIndex = _stagedProfile.DpiStages.Count - 1;
        RefreshProfileAndConfigBindings();
        MarkStagedDirty();
    }

    [RelayCommand]
    private void RemoveDpiStage()
    {
        if (!IsWriteUnlocked || _stagedProfile is null || _stagedProfile.DpiStages.Count <= 1) return;
        int idx = Math.Clamp(_stagedProfile.ActiveDpiStageIndex, 0, _stagedProfile.DpiStages.Count - 1);
        _stagedProfile.DpiStages.RemoveAt(idx);
        _stagedProfile.ActiveDpiStageIndex = Math.Clamp(idx, 0, _stagedProfile.DpiStages.Count - 1);
        RefreshProfileAndConfigBindings();
        MarkStagedDirty();
    }

    partial void OnIndependentXYChanged(bool value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.IndependentXY = value;
        if (!value)
        {
            foreach (var st in _stagedProfile.DpiStages) st.DpiY = st.Dpi;
        }
        MarkStagedDirty();
    }

    partial void OnSelectedDpiValueChanged(int value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked) return;
        int snapped = Math.Clamp((int)Math.Round(value / (double)DpiStep) * DpiStep, MinDpi, MaxDpi);
        ScheduleDpiWrite(snapped, IndependentXY ? SelectedDpiYValue : snapped);
    }

    partial void OnSelectedDpiYValueChanged(int value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || !IndependentXY) return;
        int snapped = Math.Clamp((int)Math.Round(value / (double)DpiStep) * DpiStep, MinDpi, MaxDpi);
        ScheduleDpiWrite(SelectedDpiValue, snapped);
    }

    private void ScheduleDpiWrite(int dpiX, int dpiY)
    {
        if (_stagedProfile is null) return;
        int idx = Math.Clamp(_stagedProfile.ActiveDpiStageIndex, 0, _stagedProfile.DpiStages.Count - 1);
        _stagedProfile.DpiStages[idx].Dpi = dpiX;
        _stagedProfile.DpiStages[idx].DpiY = dpiY;

        if (idx < DpiStages.Count)
        {
            DpiStages[idx].Dpi = dpiX;
            DpiStages[idx].DpiY = dpiY;
        }
        MarkStagedDirty();
    }

    // --- Staged Polling Rate & Sensor ---
    [RelayCommand]
    private void SelectPollingRate(int rateHz)
    {
        if (!IsWriteUnlocked || _stagedProfile is null || !_coordinator.ActiveCapabilities.SupportedPollingRatesHz.Contains(rateHz))
            return;

        _stagedProfile.PollingRateHz = rateHz;
        foreach (var pr in PollingRates)
            pr.IsSelected = pr.RateHz == rateHz;
        MarkStagedDirty();
    }

    [RelayCommand]
    private void SetLod(string lodStr)
    {
        if (!IsWriteUnlocked || _stagedProfile is null) return;
        if (!double.TryParse(lodStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm))
            return;
        SelectedLodMm = mm;
        _stagedProfile.LodMm = mm;
        MarkStagedDirty();
    }

    [RelayCommand]
    private void StepDebounce(string deltaStr)
    {
        if (!IsWriteUnlocked || !int.TryParse(deltaStr, out int delta)) return;
        DebounceMs = Math.Clamp(DebounceMs + delta, 0, 20);
    }

    [RelayCommand]
    private void ResetAngleTuning()
    {
        if (!IsWriteUnlocked) return;
        AngleTuningDegrees = 0;
    }

    partial void OnDebounceMsChanged(int value) => ScheduleSensorWrite();
    partial void OnAngleTuningDegreesChanged(int value) => ScheduleSensorWrite();

    private void ScheduleSensorWrite()
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.DebounceMs = Math.Clamp(DebounceMs, 0, 20);
        _stagedProfile.AngleTuningDegrees = Math.Clamp(AngleTuningDegrees, -15, 15);
        MarkStagedDirty();
    }

    partial void OnMotionSyncChanged(bool value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.MotionSync = value;
        MarkStagedDirty();
    }

    partial void OnRippleControlChanged(bool value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.RippleControl = value;
        MarkStagedDirty();
    }

    partial void OnAngleSnappingChanged(bool value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.AngleSnapping = value;
        MarkStagedDirty();
    }

    partial void OnHighSpeedModeChanged(bool value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.HighSpeedMode = value;
        MarkStagedDirty();
    }

    partial void OnSleepTimeoutMinutesChanged(int value)
    {
        if (_isSyncingFromModel || !IsWriteUnlocked || _stagedProfile is null) return;
        _stagedProfile.SleepTimeoutMinutes = value;
        MarkStagedDirty();
    }

    // --- Automation & Preferences ---
    partial void OnPinWindowOpenChanged(bool value)
    {
        PinWindowChanged?.Invoke(value);
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.PinWindowOpen = value);
    }

    partial void OnAutoSwitchEnabledChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.AutoSwitchEnabled = value);
    }

    partial void OnDefaultDesktopProfileIdChanged(string value)
    {
        if (_isSyncingFromModel || string.IsNullOrWhiteSpace(value)) return;
        _ = _coordinator.UpdateConfigAsync(c => c.DefaultDesktopProfileId = value);
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        AutostartService.SetEnabled(value);
        _ = _coordinator.UpdateConfigAsync(c => c.AutoStartEnabled = value);
    }

    partial void OnLowBatteryAlertEnabledChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.LowBatteryAlertEnabled = value);
    }

    partial void OnLowBatteryThresholdPercentChanged(int value)
    {
        if (_isSyncingFromModel) return;
        int clamped = Math.Clamp(value, 5, 40);
        _ = _coordinator.UpdateConfigAsync(c => c.LowBatteryThresholdPercent = clamped);
    }

    partial void OnFullBatteryAlertEnabledChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.FullBatteryAlertEnabled = value);
    }

    partial void OnUnlockEditingOnStartupChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.UnlockEditingOnStartup = value);
        if (value && !IsWriteUnlocked)
            IsWriteUnlocked = true;
    }

    partial void OnSelectedTrayIconStyleChanged(TrayIconStyle value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.TrayIconStyle = value);
        TrayIconStyleChanged?.Invoke(value);
    }

    partial void OnSelectedBackdropStyleChanged(FlyoutBackdropStyle value)
    {
        if (_isSyncingFromModel) return;
        _ = _coordinator.UpdateConfigAsync(c => c.BackdropStyle = value);
        BackdropChanged?.Invoke(value);
    }

    [RelayCommand]
    private async Task RefreshRunningProcessesAsync()
    {
        var procs = await Task.Run(ProcessWatcherService.GetRunningWindowExecutables);
        RunOnUi(() =>
        {
            RunningProcesses.Clear();
            foreach (var proc in procs)
                RunningProcesses.Add(proc);
        });
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        ModalDialogStateChanged?.Invoke(true);
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select .exe file",
                Filter = "Applications (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
                NewRuleProcessName = Path.GetFileName(dlg.FileName).ToLowerInvariant();
        }
        finally
        {
            ModalDialogStateChanged?.Invoke(false);
        }
    }

    [RelayCommand]
    private async Task AddProcessRuleAsync()
    {
        var normalized = ProfileAutomationService.NormalizeProcessName(NewRuleProcessName);
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(NewRuleProfileId))
            return;

        await _coordinator.UpdateConfigAsync(cfg =>
        {
            cfg.ProcessRules.RemoveAll(r => r.ProcessName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            cfg.ProcessRules.Add(new ProcessProfileRule
            {
                ProcessName = normalized,
                ProfileId = NewRuleProfileId,
                Enabled = true
            });
        });

        NewRuleProcessName = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteProcessRuleAsync(string ruleId) =>
        await _coordinator.UpdateConfigAsync(cfg => cfg.ProcessRules.RemoveAll(r => r.Id == ruleId));

    public async Task RebindHotkeyAsync(string action, uint modifiers, uint vk, string displayText)
    {
        await _coordinator.UpdateConfigAsync(cfg =>
        {
            var existing = cfg.Hotkeys.FirstOrDefault(h => h.Action == action);
            if (existing is not null)
            {
                existing.Modifiers = modifiers;
                existing.VirtualKey = vk;
                existing.DisplayText = displayText;
                existing.Enabled = true;
            }
        });

        var errors = _hotkeyService.ApplyBindings(_coordinator.Config.Hotkeys);
        HotkeyStatusText = errors.Count == 0
            ? $"Bound: {displayText}"
            : string.Join(" ", errors);
    }

    // --- HID Scanner ---
    partial void OnShowOnlyMiceAndVendorChanged(bool value) => RefreshScanner();

    partial void OnEnableVirtualMouseChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _ = Task.Run(async () =>
        {
            await _coordinator.UpdateConfigAsync(c => c.EnableVirtualMouse = value);
            await _coordinator.RefreshDevicesAsync();
            RunOnUi(RefreshScanner);
        });
    }

    partial void OnVirtualBatteryPercentChanged(int value)
    {
        if (_isSyncingFromModel) return;
        _coordinator.VirtualMouse.SimulatedBatteryPercent = value;
        _ = _coordinator.PollBatteryAsync();
    }

    partial void OnVirtualChargingChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _coordinator.VirtualMouse.SimulatedCharging = value;
        if (value) VirtualSleeping = false;
        _ = _coordinator.PollBatteryAsync();
    }

    partial void OnVirtualSleepingChanged(bool value)
    {
        if (_isSyncingFromModel) return;
        _coordinator.VirtualMouse.SimulatedSleeping = value;
        if (value) VirtualCharging = false;
        _ = _coordinator.PollBatteryAsync();
    }

    [RelayCommand]
    private void RefreshScanner()
    {
        PopulateScanner(_coordinator.ScanAllDevices());
        _ = Task.Run(async () =>
        {
            var full = await _coordinator.ScanAllDevicesAsync();
            RunOnUi(() => PopulateScanner(full));
        });
    }

    private void PopulateScanner(IReadOnlyList<MouseDeviceInfo> all)
    {
        var prevPath = SelectedScannedDevice?.Device.DevicePath;
        string? pinnedPath = _coordinator.Config.SelectedDevicePathOverride;

        var filtered = ShowOnlyMiceAndVendor
            ? all.Where(d => d.IsVirtual || d.IsBluetooth || d.IsVendorControlInterface || d.IsStandardMouseCollection || d.IsHidBatteryCollection || _registry.FindBestProtocol(d) is not null)
            : all;

        ScannedDevices.Clear();
        foreach (var dev in filtered.OrderByDescending(d => d.IsVirtual)
                                    .ThenByDescending(d => _registry.FindBestProtocol(d) is not null)
                                    .ThenByDescending(d => d.IsBluetooth)
                                    .ThenByDescending(d => d.IsVendorControlInterface && d.CanOpenReadWrite)
                                    .ThenBy(d => d.ProductName))
        {
            var proto = _registry.FindBestProtocol(dev);
            ScannedDevices.Add(new HidDeviceItemViewModel
            {
                Device = dev,
                MatchedProtocolName = proto?.DisplayName ?? "—",
                HasMatchedProtocol = proto is not null,
                IsPinned = !string.IsNullOrEmpty(pinnedPath) && dev.DevicePath.Equals(pinnedPath, StringComparison.OrdinalIgnoreCase)
            });
        }

        SelectedScannedDevice = ScannedDevices.FirstOrDefault(x => x.Device.DevicePath == prevPath)
                                ?? ScannedDevices.FirstOrDefault(x => x.HasMatchedProtocol)
                                ?? ScannedDevices.FirstOrDefault();

        ScannerStatusMessage = P($"Интерфейсов: {ScannedDevices.Count} / {all.Count}", $"Interfaces: {ScannedDevices.Count} / {all.Count}");
    }

    [RelayCommand]
    private async Task PinSelectedDeviceAsync()
    {
        if (SelectedScannedDevice is null) return;
        var path = SelectedScannedDevice.Device.DevicePath;
        await _coordinator.UpdateConfigAsync(c => c.SelectedDevicePathOverride = path);
        await _coordinator.RefreshDevicesAsync();
        RefreshScanner();
        ScannerStatusMessage = P($"Выбрано {SelectedScannedDevice.Device.DeviceKey}", $"Selected {SelectedScannedDevice.Device.DeviceKey}");
    }

    [RelayCommand]
    private async Task TestBatteryOnSelectedAsync()
    {
        if (SelectedScannedDevice is null) return;
        ScannerStatusMessage = P("Запрос заряда...", "Polling battery...");
        var reading = await _coordinator.ProbeDeviceBatteryAsync(SelectedScannedDevice.Device);
        ScannerStatusMessage = reading is null
            ? P("Нет ответа", "No response")
            : !reading.IsMouseAwake
                ? P("Мышь в спящем режиме", "Mouse is sleeping")
                : $"{reading.Percentage}%{(reading.IsCharging ? P(" (зарядка)", " (charging)") : "")}{(reading.VoltageMv.HasValue ? $" · {reading.VoltageMv} mV" : "")}";
    }

    [RelayCommand]
    private async Task SendRawPacketAsync()
    {
        if (SelectedScannedDevice is null)
        {
            ScannerStatusMessage = P("Выберите интерфейс", "Select an interface");
            return;
        }

        try
        {
            byte reportId = byte.Parse(RawReportIdHex.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var rx = await _coordinator.SendRawPacketAsync(
                SelectedScannedDevice.Device,
                UseFeatureReport,
                reportId,
                RawPayloadHex,
                SelectedChecksum,
                ReadResponseAfterSend);

            ScannerStatusMessage = rx is null
                ? P("Ошибка / таймаут", "Error / timeout")
                : rx.Length == 0
                    ? P("Отправлено", "Sent")
                    : P($"Получено {rx.Length} байт", $"Received {rx.Length} bytes");
        }
        catch (Exception ex)
        {
            ScannerStatusMessage = P($"Hex ошибка: {ex.Message}", $"Hex error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CopySelectedDeviceInfo()
    {
        if (SelectedScannedDevice is null) return;
        var d = SelectedScannedDevice.Device;
        var info = $"Product: {d.ProductName}\nVID:PID: {d.DeviceKey}\nUsage: {d.UsageHex}\nInterface: {d.InterfaceLabel}\nReports: {d.ReportLengthsSummary}\nPath: {d.DevicePath}";
        Clipboard.SetText(info);
        ScannerStatusMessage = P("Скопировано в буфер", "Copied to clipboard");
    }

    [RelayCommand]
    private void ClearLogs() => PacketLogs.Clear();

    [RelayCommand]
    private void CopyLogs()
    {
        if (PacketLogs.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, PacketLogs.Select(l => l.FormattedLine)));
        ScannerStatusMessage = P("Лог скопирован", "Log copied");
    }

    [RelayCommand]
    private async Task RefreshNowAsync()
    {
        RefreshWindowsSettings();
        await _coordinator.RefreshDevicesAsync();
        RefreshScanner();
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(_configStore.ConfigDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _configStore.ConfigDirectory) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke();

    private static string FormatCleanDeviceName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Mouse";
        var cleaned = rawName
            .Replace(" DONGLE", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" RECEIVER", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" WIRELESS", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" (Demo)", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? rawName.Trim() : cleaned;
    }

    private static string FormatShortProfileName(MouseProfile profile) => profile.Id switch
    {
        "device-read" => P("Мышь", "Mouse"),
        "eco" => P("Эко", "Eco"),
        "gaming" => P("Игровой", "Gaming"),
        "esports" => "4K",
        _ => profile.Name.Length <= 8 ? profile.Name : profile.Name[..8].Trim()
    };

    private static void RunOnUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess())
            action();
        else
            disp.BeginInvoke(action);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _lockedHintCts?.Cancel();
        _lockedHintCts?.Dispose();
        _pollingTester.PollingRateUpdated -= OnRawPollingRateUpdated;
        _pollingTester.Stop();
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.PacketLogged -= OnPacketLogged;
        _coordinator.ConfigChanged -= OnCoordinatorConfigChanged;
    }
}
