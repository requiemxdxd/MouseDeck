using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MouseDeck.App.ViewModels;
using MouseDeck.Core.Models;
using MouseDeck.Win32.Automation;
using MouseDeck.Win32.Windowing;

namespace MouseDeck.App.Views;

public partial class FlyoutWindow : Window
{
    private readonly MainFlyoutViewModel _viewModel;
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly DispatcherTimer _focusWatchTimer;
    private FlyoutPositioner.RECT? _lastAnchorRect;
    private bool _suppressDeactivate;
    private bool _isPinned;
    private bool _isClosing;
    private double _targetLeft;
    private double _targetTop;
    private int _animVersion;
    private DateTime _lastHiddenByDeactivateUtc = DateTime.MinValue;
    private DateTime _lastShownUtc = DateTime.MinValue;

    public FlyoutWindow(MainFlyoutViewModel viewModel, FlyoutBackdropStyle initialBackdrop, bool initialPinned)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _isPinned = initialPinned;
        DataContext = _viewModel;

        // Only trim working set after the flyout has stayed closed for 60s so rapid reopens
        // never suffer hard page-fault stalls during the open animation.
        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _idleTrimTimer.Tick += (_, _) =>
        {
            _idleTrimTimer.Stop();
            if (!IsVisible)
                UserIdleHelper.TrimWorkingSet();
        };

        // Ensures the flyout never gets stuck in an inactive "IsVisible=true" zombie state
        // if an elevated or fullscreen window prevented initial focus or swallowed Deactivated.
        _focusWatchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _focusWatchTimer.Tick += (_, _) => CheckForegroundFocusWhileOpen();

        ApplySurfaceStyle(initialBackdrop);

        _viewModel.BackdropChanged += ApplySurfaceStyle;
        _viewModel.PinWindowChanged += pinned =>
        {
            _isPinned = pinned;
            Topmost = true;
        };
        _viewModel.ModalDialogStateChanged += isOpen => _suppressDeactivate = isOpen;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += OnFlyoutSizeChanged;
    }

    public bool AllowClose { get; set; }
    public Func<bool>? IsCursorOverIndicator { get; set; }
    public Func<IntPtr>? GetIndicatorHandle { get; set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            DwmBackdropHelper.ApplyCrispWin11Chrome(hwnd);
        }
    }

    private void ApplySurfaceStyle(FlyoutBackdropStyle style)
    {
        var brush = style == FlyoutBackdropStyle.Acrylic
            ? (Brush)FindResource("SurfaceWindowSolidBrush")
            : (Brush)FindResource("SurfaceCardBrush");
        Background = brush;
        RootContainer.Background = brush;
        MainChromeBorder.Background = brush;
    }

    public void ToggleFlyout(FlyoutPositioner.RECT? trayIconRect)
    {
        if (IsVisible && !_isClosing)
        {
            HideFlyout(fromDeactivate: false);
            return;
        }

        // Guard ONLY against the classic tray-icon deactivate->click race (never block TaskbarPill toggles)
        if (!IsVisible && IsCursorOverIndicator?.Invoke() != true &&
            (DateTime.UtcNow - _lastHiddenByDeactivateUtc).TotalMilliseconds < 120)
        {
            return;
        }

        ShowFlyout(trayIconRect);
    }

    public void ShowFlyout(FlyoutPositioner.RECT? trayIconRect)
    {
        _idleTrimTimer.Stop();
        _lastAnchorRect = trayIconRect;

        bool wasClosing = _isClosing && IsVisible;
        _isClosing = false;
        int version = ++_animVersion;

        BeginAnimation(TopProperty, null);
        RootContainer.BeginAnimation(OpacityProperty, null);

        ComputeTargetAnchor();
        double startTop = wasClosing ? Top : (_targetTop + 10.0);
        double startOpacity = wasClosing ? RootContainer.Opacity : 0.0;

        Left = _targetLeft;
        Top = startTop;
        Topmost = true;
        RootContainer.Opacity = startOpacity;
        _lastShownUtc = DateTime.UtcNow;

        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            DwmBackdropHelper.ApplyCrispWin11Chrome(hwnd);
            DwmBackdropHelper.ForceForegroundWindow(hwnd);
        }

        Activate();
        Focus();
        _focusWatchTimer.Start();

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (version != _animVersion || _isClosing || !IsVisible)
                return;

            PlayShowAnimation(version, startTop, startOpacity);
        });
    }

    public void HideFlyout(bool fromDeactivate = false)
    {
        _focusWatchTimer.Stop();
        if (!IsVisible || _isClosing) return;

        _isClosing = true;
        int version = ++_animVersion;
        if (fromDeactivate)
            _lastHiddenByDeactivateUtc = DateTime.UtcNow;

        _viewModel.OnFlyoutClosed();

        int fps = FlyoutPositioner.GetDisplayRefreshRateHz();
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var slide = new DoubleAnimation
        {
            From = Top,
            To = _targetTop + 8.0,
            Duration = TimeSpan.FromMilliseconds(105),
            EasingFunction = easeIn
        };
        var fade = new DoubleAnimation
        {
            From = RootContainer.Opacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(95),
            EasingFunction = easeIn
        };

        Timeline.SetDesiredFrameRate(slide, fps);
        Timeline.SetDesiredFrameRate(fade, fps);

        slide.Completed += (_, _) =>
        {
            if (version != _animVersion)
                return;

            _isClosing = false;
            BeginAnimation(TopProperty, null);
            RootContainer.BeginAnimation(OpacityProperty, null);
            Top = _targetTop;
            RootContainer.Opacity = 1.0;
            Hide();
            _idleTrimTimer.Stop();
            _idleTrimTimer.Start();
        };

        BeginAnimation(TopProperty, slide);
        RootContainer.BeginAnimation(OpacityProperty, fade);
    }

    private void PlayShowAnimation(int version, double startTop, double startOpacity)
    {
        int fps = FlyoutPositioner.GetDisplayRefreshRateHz();

        var slideEase = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };
        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = startTop,
            To = _targetTop,
            Duration = TimeSpan.FromMilliseconds(195),
            EasingFunction = slideEase
        };
        var fade = new DoubleAnimation
        {
            From = startOpacity,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(130),
            EasingFunction = fadeEase
        };

        Timeline.SetDesiredFrameRate(slide, fps);
        Timeline.SetDesiredFrameRate(fade, fps);

        slide.Completed += (_, _) =>
        {
            if (version != _animVersion)
                return;

            BeginAnimation(TopProperty, null);
            RootContainer.BeginAnimation(OpacityProperty, null);
            Top = _targetTop;
            RootContainer.Opacity = 1.0;

            _viewModel.OnFlyoutOpened();
        };

        BeginAnimation(TopProperty, slide);
        RootContainer.BeginAnimation(OpacityProperty, fade);
    }

    private void ComputeTargetAnchor()
    {
        double effW = !double.IsNaN(ActualWidth) && ActualWidth >= 100
            ? ActualWidth
            : (!double.IsNaN(Width) && Width >= 100 ? Width : 360);
        double effH = !double.IsNaN(ActualHeight) && ActualHeight >= 100
            ? ActualHeight
            : (!double.IsNaN(Height) && Height >= 100 ? Height : Math.Max(MinHeight, 540));

        (_targetLeft, _targetTop) = FlyoutPositioner.ComputePosition(effW, effH, _lastAnchorRect);
    }

    private void OnFlyoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || _isClosing)
            return;

        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1.0)
        {
            ComputeTargetAnchor();
            Left = _targetLeft;
            Top = _targetTop;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible || _isClosing) return;

        if (e.PropertyName == nameof(MainFlyoutViewModel.IsSettingsOpen))
        {
            if (_viewModel.IsSettingsOpen)
                AnimateHorizontalReveal(SettingsScreenGrid, SettingsScreenSlide, fromX: 12.0, durationMs: 155);
            else
                AnimateHorizontalReveal(MainScreenGrid, MainScreenSlide, fromX: -12.0, durationMs: 155);
        }
        else if (e.PropertyName == nameof(MainFlyoutViewModel.SettingsTabIndex) && _viewModel.IsSettingsOpen)
        {
            AnimateHorizontalReveal(SettingsTabsHost, SettingsTabsSlide, fromX: 6.0, durationMs: 120);
        }
        else if (e.PropertyName == nameof(MainFlyoutViewModel.IsBatteryHistoryExpanded) && _viewModel.IsBatteryHistoryExpanded)
        {
            AnimateVerticalReveal(BatteryHistoryDrawer, BatteryHistorySlide, fromY: -6.0, durationMs: 155);
        }
    }

    private static void AnimateHorizontalReveal(UIElement target, TranslateTransform transform, double fromX, int durationMs)
    {
        int fps = FlyoutPositioner.GetDisplayRefreshRateHz();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = fromX,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = ease
        };
        var fade = new DoubleAnimation
        {
            From = 0.25,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(Math.Max(90, durationMs - 20)),
            EasingFunction = ease
        };

        Timeline.SetDesiredFrameRate(slide, fps);
        Timeline.SetDesiredFrameRate(fade, fps);

        slide.Completed += (_, _) =>
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0.0;
        };
        fade.Completed += (_, _) =>
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = 1.0;
        };

        transform.BeginAnimation(TranslateTransform.XProperty, slide);
        target.BeginAnimation(OpacityProperty, fade);
    }

    private static void AnimateVerticalReveal(UIElement target, TranslateTransform transform, double fromY, int durationMs)
    {
        int fps = FlyoutPositioner.GetDisplayRefreshRateHz();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = fromY,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = ease
        };
        var fade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = ease
        };

        Timeline.SetDesiredFrameRate(slide, fps);
        Timeline.SetDesiredFrameRate(fade, fps);

        slide.Completed += (_, _) =>
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0.0;
        };
        fade.Completed += (_, _) =>
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = 1.0;
        };

        transform.BeginAnimation(TranslateTransform.YProperty, slide);
        target.BeginAnimation(OpacityProperty, fade);
    }

    private void OnBatteryCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.HasBatterySupport)
        {
            _viewModel.ToggleBatteryHistoryCommand.Execute(null);
        }
    }

    private void OnHardwareAreaPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsWriteUnlocked)
        {
            _viewModel.TriggerLockedHint();
        }
    }

    private void OnDpiBlockMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.NudgeDpiByWheel(e.Delta);
        e.Handled = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate || _isPinned || _isClosing) return;
        // If user clicked directly on the Taskbar Pill, let the pill's LeftClicked/RightClicked handler manage it
        if (IsCursorOverIndicator?.Invoke() == true) return;
        HideFlyout(fromDeactivate: true);
    }

    private void CheckForegroundFocusWhileOpen()
    {
        if (!IsVisible || _isClosing || _isPinned || _suppressDeactivate)
            return;

        if ((DateTime.UtcNow - _lastShownUtc).TotalMilliseconds < 320)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr fg = DwmBackdropHelper.GetForegroundWindow();
        if (fg != hwnd)
        {
            HideFlyout(fromDeactivate: true);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSettingsOpen)
                _viewModel.IsSettingsOpen = false;
            else
                HideFlyout(fromDeactivate: false);
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideFlyout(fromDeactivate: false);
        }
    }
}
