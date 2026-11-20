using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MouseDeck.App.ViewModels;
using MouseDeck.App.Views;
using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Services;
using MouseDeck.Core.Transport;
using MouseDeck.Win32.Automation;
using MouseDeck.Win32.Hid;
using MouseDeck.Win32.Tray;
using MouseDeck.Win32.Windowing;

namespace MouseDeck.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\MouseDeck_SingleInstance_Mutex_v1";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;
    private MessageWindow? _messageWindow;
    private NativeNotifyIcon? _trayIcon;
    private TaskbarPillWidget? _taskbarPill;
    private DeviceCoordinator? _coordinator;
    private ProfileAutomationService? _automation;
    private ProcessWatcherService? _processWatcher;
    private GlobalHotkeyService? _hotkeyService;
    private RawInputPollingTester? _pollingTester;
    private FlyoutWindow? _flyoutWindow;
    private CancellationTokenSource? _pnpDebounceCts;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageWindow.BroadcastWakeupToExistingInstance();
            Shutdown(0);
            return;
        }

        // Global resilience: log unexpected exceptions to rotating app.log instead of crashing silently
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("UI", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogger.Error("Domain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Task", args.Exception);
            args.SetObserved();
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _messageWindow = _services.GetRequiredService<MessageWindow>();
        _trayIcon = _services.GetRequiredService<NativeNotifyIcon>();
        _taskbarPill = _services.GetRequiredService<TaskbarPillWidget>();
        _coordinator = _services.GetRequiredService<DeviceCoordinator>();
        _automation = _services.GetRequiredService<ProfileAutomationService>();
        _processWatcher = _services.GetRequiredService<ProcessWatcherService>();
        _hotkeyService = _services.GetRequiredService<GlobalHotkeyService>();
        _pollingTester = _services.GetRequiredService<RawInputPollingTester>();

        _coordinator.ConfigureUserIdleProvider(UserIdleHelper.GetIdleDuration);
        AppLocalization.SetLanguage(_coordinator.Config.Language);
        AutostartService.RefreshPathIfEnabled();

        // Wire taskbar pill + fallback tray icon (zero duplicate icons on taskbar!)
        SyncIndicators(_coordinator.State, _coordinator.Config.TrayIconStyle);

        _taskbarPill.LeftClicked += () => Dispatcher.BeginInvoke(() => GetOrCreateFlyout().ToggleFlyout(GetIndicatorAnchorRect()));
        _taskbarPill.RightClicked += () => Dispatcher.BeginInvoke(ShowTrayContextMenu);
        _taskbarPill.AttachmentStateChanged += isAttached =>
            Dispatcher.BeginInvoke(() => _trayIcon.Update(_coordinator.State, _coordinator.Config.TrayIconStyle, hidden: isAttached));

        _trayIcon.LeftClicked += () => Dispatcher.BeginInvoke(() => GetOrCreateFlyout().ToggleFlyout(GetIndicatorAnchorRect()));
        _trayIcon.NotificationClicked += () => Dispatcher.BeginInvoke(() => GetOrCreateFlyout().ShowFlyout(GetIndicatorAnchorRect()));
        _trayIcon.RightClicked += () => Dispatcher.BeginInvoke(ShowTrayContextMenu);

        // Wire live state -> indicators & toast notifications
        _coordinator.StateChanged += state =>
        {
            Dispatcher.BeginInvoke(() => SyncIndicators(state, _coordinator.Config.TrayIconStyle));
        };

        _coordinator.LowBatteryAlertTriggered += (deviceName, pct) =>
        {
            Dispatcher.BeginInvoke(() =>
                _trayIcon.ShowNotification(
                    AppLocalization.Pick($"Низкий заряд мыши ({pct}%)", $"Low mouse battery ({pct}%)"),
                    AppLocalization.Pick($"{deviceName}: подключите USB-кабель для зарядки.", $"{deviceName}: connect USB cable to charge."),
                    isWarning: true));
        };

        _coordinator.FullBatteryAlertTriggered += deviceName =>
        {
            Dispatcher.BeginInvoke(() =>
                _trayIcon.ShowNotification(
                    AppLocalization.Pick("Мышь заряжена (100%)", "Mouse fully charged (100%)"),
                    AppLocalization.Pick($"{deviceName}: зарядка завершена, можно отключить кабель.", $"{deviceName}: charging complete, you can unplug the cable."),
                    isWarning: false));
        };

        // Wire zero-CPU Win32 PnP USB/HID arrival & removal events
        _messageWindow.HidDeviceTopologyChanged += isArrival =>
        {
            _pnpDebounceCts?.Cancel();
            _pnpDebounceCts?.Dispose();
            _pnpDebounceCts = new CancellationTokenSource();
            var ct = _pnpDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, ct);
                    await _coordinator.RefreshDevicesAsync(ct);
                }
                catch (OperationCanceledException) { }
            });
        };

        _messageWindow.TaskbarRecreated += () => Dispatcher.BeginInvoke(() =>
        {
            _taskbarPill.OnTaskbarOrDisplayChanged();
            SyncIndicators(_coordinator.State, _coordinator.Config.TrayIconStyle);
        });

        _messageWindow.DisplayOrSettingsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _taskbarPill.OnTaskbarOrDisplayChanged();
            SyncIndicators(_coordinator.State, _coordinator.Config.TrayIconStyle);
        });

        _messageWindow.SessionEnding += () => Dispatcher.BeginInvoke(ExitCleanly);

        _messageWindow.SingleInstanceWakeupRequested += () =>
        {
            Dispatcher.BeginInvoke(() => GetOrCreateFlyout().ShowFlyout(GetIndicatorAnchorRect()));
        };

        // Windows Suspend/Resume lifecycle (prevents false disconnects after PC sleep)
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Wire SetWinEventHook foreground process switcher (0% CPU in idle)
        _processWatcher.InitialProcessObserved += exe => _coordinator.UpdateActiveForegroundProcess(exe);
        _processWatcher.ForegroundProcessChanged += exe =>
        {
            _automation.OnForegroundProcessChanged(exe);
            Dispatcher.BeginInvoke(() => _taskbarPill.EnsureTopmostOrder());
        };
        _processWatcher.Start();

        // Wire global hotkeys (RegisterHotKey)
        _hotkeyService.HotkeyTriggered += action => _ = _automation.HandleHotkeyActionAsync(action);
        _hotkeyService.ApplyBindings(_coordinator.Config.Hotkeys);

        // Optional immediate display for CLI/preview flags (--show, --settings=N) or first-time setup
        bool explicitShow = e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
        bool launchedMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        bool needsFirstRunSetup = _services.GetRequiredService<JsonConfigStore>().WasCreatedOnLastLoad ||
                                  !_coordinator.Config.IsFirstRunSetupCompleted;

        if (explicitShow)
        {
            var tabArg = e.Args.FirstOrDefault(a => a.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase));
            var flyout = GetOrCreateFlyout();
            if (tabArg is not null && int.TryParse(tabArg[11..], out int tabIdx) && flyout.DataContext is MainFlyoutViewModel vm)
            {
                vm.IsFirstRunSetupVisible = false;
                vm.IsSettingsOpen = true;
                vm.SettingsTabIndex = Math.Clamp(tabIdx, 0, 3);
            }
            flyout.ShowFlyout(GetIndicatorAnchorRect());
        }
        else if (needsFirstRunSetup && !launchedMinimized)
        {
            // On first run / pending initial setup, open the flyout right away so the user can choose language & verify mouse
            Dispatcher.BeginInvoke(() => GetOrCreateFlyout().ShowFlyout(GetIndicatorAnchorRect()));
        }

        // Start HID + Bluetooth coordinator asynchronously so UI & taskbar indicator appear in <50ms
        _ = Task.Run(async () =>
        {
            try
            {
                await _coordinator.StartAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Startup", ex);
            }
            finally
            {
                if (!explicitShow && !needsFirstRunSetup)
                {
                    UserIdleHelper.TrimWorkingSet();
                }
            }
        });
    }

    private void SyncIndicators(MouseRuntimeState state, TrayIconStyle style)
    {
        if (_taskbarPill is null || _trayIcon is null) return;

        bool pillAttached = _taskbarPill.Update(state, style);
        // Hide the classic 16x16 tray icon while the Taskbar Pill is visible so there is NEVER a duplicate indicator,
        // while keeping the tray icon registered (NIS_HIDDEN) for Windows toast notifications.
        _trayIcon.Update(state, style, hidden: pillAttached);
    }

    private FlyoutPositioner.RECT? GetIndicatorAnchorRect()
    {
        return _taskbarPill?.GetScreenRect() ?? _trayIcon?.GetIconRect();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_coordinator is null) return;
        if (e.Mode == PowerModes.Suspend)
        {
            _coordinator.OnSystemSuspended();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _ = _coordinator.OnSystemResumedAsync();
            Dispatcher.BeginInvoke(() => _taskbarPill?.OnTaskbarOrDisplayChanged());
        }
    }

    private FlyoutWindow GetOrCreateFlyout()
    {
        if (_flyoutWindow is not null)
            return _flyoutWindow;

        var mainVm = _services!.GetRequiredService<MainFlyoutViewModel>();
        mainVm.TrayIconStyleChanged += style =>
        {
            if (_coordinator is not null)
                SyncIndicators(_coordinator.State, style);
        };
        mainVm.ExitRequested += ExitCleanly;

        _flyoutWindow = new FlyoutWindow(mainVm, _coordinator!.Config.BackdropStyle, _coordinator.Config.PinWindowOpen)
        {
            IsCursorOverIndicator = () => _taskbarPill?.IsCursorOverPill() == true,
            GetIndicatorHandle = () => _taskbarPill?.Handle ?? IntPtr.Zero
        };
        return _flyoutWindow;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var configStore = new JsonConfigStore();
        services.AddSingleton(configStore);
        services.AddSingleton<IHidTransport, Win32HidTransport>();

        var virtualProtocol = new VirtualMockProtocol();
        services.AddSingleton(virtualProtocol);

        // Universal 6-tier protocol stack (zero driver modifications, zero vendor-software conflicts)
        services.AddSingleton<IMouseProtocol, CompXProtocol>();
        services.AddSingleton<IMouseProtocol, LogitechHidPlusPlusProtocol>();
        services.AddSingleton<IMouseProtocol, RazerHidProtocol>();
        services.AddSingleton<IMouseProtocol, NordicHidProtocol>();
        services.AddSingleton<IMouseProtocol, MajorBrandsBatteryProtocol>();
        services.AddSingleton<IMouseProtocol, BluetoothMouseProtocol>();
        services.AddSingleton<IMouseProtocol, GenericMouseProtocol>();
        services.AddSingleton<IMouseProtocol>(virtualProtocol);

        services.AddSingleton(sp =>
            new ProtocolRegistry(sp.GetServices<IMouseProtocol>(), configStore.ProtocolsDirectory));

        services.AddSingleton<DeviceCoordinator>();
        services.AddSingleton<ProfileAutomationService>();
        services.AddSingleton<MessageWindow>();
        services.AddSingleton<NativeNotifyIcon>();
        services.AddSingleton<TaskbarPillWidget>();
        services.AddSingleton<ProcessWatcherService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<RawInputPollingTester>();
        services.AddSingleton<MainFlyoutViewModel>();
    }

    private void ShowTrayContextMenu()
    {
        if (_coordinator is null || _trayIcon is null)
            return;

        var state = _coordinator.State;
        var menu = new ContextMenu();

        string P(string ru, string en) => AppLocalization.Pick(ru, en);

        string pctText = state.BatteryPercent.HasValue ? $"{state.BatteryPercent}%" : "—";
        string voltText = state.VoltageMv is int mv and > 2500 ? $" · {mv / 1000.0:0.00} {P("В", "V")}" : string.Empty;
        var header = new MenuItem
        {
            Header = $"{state.DeviceName} ({pctText}{voltText})",
            IsEnabled = false
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        var openItem = new MenuItem { Header = P("Открыть MouseDeck", "Open Flyout") };
        openItem.Click += (_, _) => GetOrCreateFlyout().ShowFlyout(GetIndicatorAnchorRect());
        menu.Items.Add(openItem);

        // Multi-mouse device switcher when 2+ mice are present
        if (state.AvailableMice.Count > 1)
        {
            var devicesMenu = new MenuItem { Header = P($"Устройства ({state.AvailableMice.Count})", $"Devices ({state.AvailableMice.Count})") };
            foreach (var m in state.AvailableMice)
            {
                var dItem = new MenuItem
                {
                    Header = $"{m.DisplayName} ({m.ProtocolName})",
                    IsChecked = m.IsSelected
                };
                string path = m.DevicePath;
                dItem.Click += (_, _) => _ = _coordinator.SelectDeviceAsync(path);
                devicesMenu.Items.Add(dItem);
            }
            menu.Items.Add(devicesMenu);
        }

        // Quick profile submenu (for mice supporting hardware profiles)
        if (_coordinator.ActiveCapabilities.SupportsHardwareSettings)
        {
            var profilesMenu = new MenuItem { Header = P("Быстрый профиль", "Quick Profile") };
            foreach (var profile in _coordinator.Config.Profiles)
            {
                var pItem = new MenuItem
                {
                    Header = $"{profile.Name} ({profile.PollingRateHz} Hz · {profile.CurrentDpi} DPI)",
                    IsChecked = profile.Id == _coordinator.Config.ActiveProfileId
                };
                var id = profile.Id;
                pItem.Click += (_, _) =>
                {
                    if (_coordinator.IsWriteUnlocked)
                        _ = _coordinator.SelectProfileAsync(id, ProfileSwitchSource.Manual);
                    else
                        GetOrCreateFlyout().ShowFlyout(GetIndicatorAnchorRect());
                };
                profilesMenu.Items.Add(pItem);
            }
            menu.Items.Add(profilesMenu);

            var autoSwitchItem = new MenuItem
            {
                Header = P("Автопрофиль по .exe", "Auto-switch by .exe"),
                IsCheckable = true,
                IsChecked = _coordinator.Config.AutoSwitchEnabled
            };
            autoSwitchItem.Click += (_, _) =>
            {
                bool next = autoSwitchItem.IsChecked;
                _ = _coordinator.UpdateConfigAsync(c => c.AutoSwitchEnabled = next);
            };
            menu.Items.Add(autoSwitchItem);
        }

        var refreshItem = new MenuItem { Header = P("Обновить заряд и устройства", "Refresh Battery & Devices") };
        refreshItem.Click += (_, _) => _ = _coordinator.RefreshDevicesAsync();
        menu.Items.Add(refreshItem);

        var settingsItem = new MenuItem { Header = P("Настройки и диагностика", "Settings & Diagnostics") };
        settingsItem.Click += (_, _) =>
        {
            var flyout = GetOrCreateFlyout();
            if (flyout.DataContext is MainFlyoutViewModel vm)
                vm.IsSettingsOpen = true;
            flyout.ShowFlyout(GetIndicatorAnchorRect());
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var langItem = new MenuItem
        {
            Header = P("Language: English (EN)", "Язык: Русский (RU)")
        };
        langItem.Click += (_, _) =>
        {
            var flyout = GetOrCreateFlyout();
            if (flyout.DataContext is MainFlyoutViewModel vm)
                vm.ToggleLanguageCommand.Execute(null);
        };
        menu.Items.Add(langItem);

        var autostartItem = new MenuItem
        {
            Header = P("Автозапуск с Windows", "Start with Windows"),
            IsCheckable = true,
            IsChecked = AutostartService.IsEnabled()
        };
        autostartItem.Click += (_, _) =>
        {
            bool next = autostartItem.IsChecked;
            AutostartService.SetEnabled(next);
            _ = _coordinator.UpdateConfigAsync(c => c.AutoStartEnabled = next);
        };
        menu.Items.Add(autostartItem);

        var configItem = new MenuItem { Header = P("Открыть папку конфигов", "Open Config Folder") };
        configItem.Click += (_, _) =>
        {
            var store = _services?.GetService<JsonConfigStore>();
            if (store is not null)
            {
                Directory.CreateDirectory(store.ConfigDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", store.ConfigDirectory) { UseShellExecute = true });
            }
        };
        menu.Items.Add(configItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = P("Выход", "Exit") };
        exitItem.Click += (_, _) => ExitCleanly();
        menu.Items.Add(exitItem);

        DwmBackdropHelper.SetForegroundWindow(_messageWindow!.Handle);
        menu.IsOpen = true;
    }

    private async void ExitCleanly()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            if (_flyoutWindow is not null)
            {
                _flyoutWindow.AllowClose = true;
                _flyoutWindow.Close();
            }

            _pollingTester?.Dispose();
            _pnpDebounceCts?.Cancel();
            _pnpDebounceCts?.Dispose();
            _processWatcher?.Dispose();
            _hotkeyService?.Dispose();
            _automation?.Dispose();
            _taskbarPill?.Dispose();
            _trayIcon?.Dispose();

            if (_coordinator is not null)
                await _coordinator.DisposeAsync();

            _messageWindow?.Dispose();
            if (_services is not null)
                await _services.DisposeAsync();

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Shutdown", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }
}
