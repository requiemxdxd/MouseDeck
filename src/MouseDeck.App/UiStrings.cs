using CommunityToolkit.Mvvm.ComponentModel;
using MouseDeck.Core.Models;
using MouseDeck.Core.Services;

namespace MouseDeck.App;

/// <summary>
/// Live observable bilingual (Russian / English) string dictionary bound directly in XAML.
/// Raises PropertyChanged("") whenever AppLocalization.CurrentLanguage changes so the entire
/// FlyoutWindow and all 4 Settings tabs switch languages in &lt;1ms without reopening.
/// </summary>
public sealed class UiStrings : ObservableObject
{
    public static UiStrings Instance { get; } = new();

    private UiStrings()
    {
        AppLocalization.LanguageChanged += _ => Refresh();
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private static string P(string ru, string en) => AppLocalization.Pick(ru, en);

    // --- Header & Language Badge ---
    public bool IsRussian => AppLocalization.IsRussian;
    public bool IsEnglish => !AppLocalization.IsRussian;
    public string LanguageBadge => P("RU", "EN");
    public string LanguageTooltip => P("Язык интерфейса: Русский (нажмите для EN)", "Language: English (click for RU)");
    public string ReadBadge => P("Чтение", "Read");
    public string WriteBadge => P("Запись", "Write");
    public string LockTooltip => P(
        "Защита памяти мыши: Чтение = блокировка случайных записей, Запись = редактирование",
        "Onboard memory protection: Read = prevent accidental writes, Write = allow edits");
    public string PinTooltip => P("Закрепить окно", "Pin window");
    public string SettingsTooltip => P("Настройки и диагностика", "Settings & Diagnostics");

    // --- First-Run Setup Overlay ---
    public string SetupTitle => P("Первоначальная настройка", "Initial Setup");
    public string SetupSubtitle => P("Проверьте язык интерфейса и вашу мышь", "Choose your language and verify your mouse");
    public string SetupLanguageHeader => P("1. Язык интерфейса", "1. Interface Language");
    public string SetupMouseHeader => P("2. Определённая мышь", "2. Detected Mouse");
    public string SetupMouseQuestion => P("Мышь определена правильно?", "Is your mouse detected correctly?");
    public string SetupSelectMouseHint => P("Если подключено несколько устройств, выберите нужное:", "If multiple devices are connected, pick your primary mouse:");
    public string SetupRescanBtn => P("Пересканировать", "Rescan");
    public string SetupManualHidBtn => P("Выбрать интерфейс в HID-сканере", "Pick interface in HID Scanner");
    public string SetupConfirmBtn => P("Да, всё верно — начать", "Yes, looks good — start");

    // --- Main Screen: Battery, Profile, DPI, Polling, Quick Sensor ---
    public string BatteryCardTooltip => P("Нажмите, чтобы открыть историю разряда за 24 ч", "Click to toggle 24h discharge history");
    public string DischargeHistory24h => P("История разряда (24 ч)", "Discharge history (24h)");
    public string NotEnoughSparklineData => P("Недостаточно точек для графика (накапливается в фоне)", "Not enough data yet (collecting in background)");
    public string MemoryProtectionBanner => P("Включена защита памяти (только чтение)", "Memory protection active (read-only)");
    public string UnlockBtn => P("Открыть", "Unlock");
    public string ProfileHeader => P("Профиль", "Profile");
    public string AutoHeader => P("Авто", "Auto");
    public string SensitivityDpi => P("Чувствительность (DPI)", "Sensitivity (DPI)");
    public string DpiWheelTooltip => P("Крутите колесо мыши для шага ±50 DPI", "Scroll mouse wheel to step ±50 DPI");
    public string PollingRateHeader => P("Частота опроса", "Polling Rate");
    public string LodHeader => P("Высота отрыва (LOD)", "Lift-off Distance (LOD)");
    public string Lod1Mm => P("1 мм", "1 mm");
    public string Lod2Mm => P("2 мм", "2 mm");
    public string ClickDebounceHeader => P("Задержка клика (Debounce)", "Click Debounce");
    public string MsSuffix => P(" мс", " ms");
    public string WindowsPointerSpeedMain => P("Скорость курсора Windows", "Windows Pointer Speed");
    public string EnhancePointerPrecisionMain => P("Повышенная точность (акселерация)", "Enhance pointer precision (acceleration)");
    public string PollingRateTesterMain => P("Тестер частоты опроса (Raw Input)", "Polling Rate Tester (Raw Input)");
    public string TestHzBtn => P("Тест Hz", "Test Hz");
    public string StopBtn => P("Стоп", "Stop");
    public string RevertBtn => P("Откатить", "Revert");
    public string ApplyBtn => P("Применить", "Apply");

    // --- Settings Screen Header & Navigation ---
    public string BackTooltip => P("Назад (Esc)", "Back (Esc)");
    public string SettingsHeader => P("Настройки", "Settings");
    public string RefreshDevicesTooltip => P("Обновить устройства", "Refresh devices");
    public string OpenConfigFolderTooltip => P("Открыть папку конфигов", "Open config folder");
    public string SetupWizardTooltip => P("Мастер настройки и выбора мыши", "Language & mouse setup wizard");
    public string QuitTooltip => P("Выйти из MouseDeck", "Quit MouseDeck");
    public string TabSensor => P("Сенсор", "Sensor");
    public string TabAuto => P("Авто", "Auto");
    public string TabOs => P("ОС", "OS");
    public string TabHid => "HID";

    // --- Tab 0: SensorTabView ---
    public string ProfileAndMemory => P("Профиль и память", "Profile & Memory");
    public string SaveBtn => P("Сохранить", "Save");
    public string DeleteProfileTooltip => P("Удалить профиль", "Delete profile");
    public string NewBtn => P("Новый", "New");
    public string ImportBtn => P("Импорт", "Import");
    public string ExportBtn => P("Экспорт", "Export");
    public string ReadBtn => P("Считать", "Read");
    public string ReadRegistersTooltip => P("Считать текущие регистры с мыши", "Read current registers from mouse");
    public string IndependentDpiXY => P("Раздельный DPI (оси X / Y)", "Independent DPI (X / Y)");
    public string YAxisSensitivity => P("Чувствительность по оси Y", "Y-Axis Sensitivity");
    public string AddDpiStageBtn => P("+ Ступень DPI", "+ DPI Stage");
    public string RemoveDpiStageBtn => P("− Ступень DPI", "− DPI Stage");
    public string AngleTuningHeader => P("Поворот оси (Angle Tuning)", "Angle Tuning");
    public string ResetBtn => P("Сброс", "Reset");
    public string RippleControlHeader => P("Сглаживание (Ripple Control)", "Ripple Control");
    public string AngleSnappingHeader => P("Выпрямление (Angle Snapping)", "Angle Snapping");
    public string HighSpeedModeHeader => P("Турбо-режим сенсора (20K FPS)", "Sensor High-Speed Mode (20K FPS)");
    public string SleepTimerHeader => P("Таймер сна (мин)", "Sleep Timer (min)");

    // --- Tab 1: AutomationTabView ---
    public string DefaultProfileHeader => P("Базовый профиль", "Default Profile");
    public string ProcessPlaceholder => P("процесс.exe", "process.exe");
    public string BrowseExeTooltip => P("Выбрать .exe файл", "Browse .exe file");
    public string AddRuleTooltip => P("Добавить правило", "Add rule");
    public string HotkeyBoxTooltip => P("Кликните и нажмите комбинацию клавиш", "Click and press a key combination");
    public string StartWithWindowsHeader => P("Автозапуск с Windows", "Start with Windows");
    public string LowBatteryAlertPrefix => P("Уведомление при разряде < ", "Low battery alert < ");
    public string FullBatteryAlertHeader => P("Уведомление о зарядке 100%", "Notify when charged to 100%");
    public string UnlockOnStartupHeader => P("Редактирование без замка на старте", "Unlock editing on startup");
    public string BatteryIndicatorHeader => P("Индикатор заряда", "Battery indicator");
    public string WindowSurfaceHeader => P("Поверхность окна", "Window surface");

    // --- Tab 2: SystemAndButtonsTabView ---
    public string WindowsPointerSection => P("Указатель Windows", "Windows Pointer");
    public string RefreshBtn => P("Обновить", "Refresh");
    public string PointerSpeedRow => P("Скорость курсора", "Pointer Speed");
    public string ScrollLinesRow => P("Строк прокрутки", "Scroll Lines");
    public string ScrollLinesUnit => P(" стр", " lines");
    public string DoubleClickRow => P("Двойной клик", "Double-Click");
    public string EnhancePointerPrecisionShort => P("Повышенная точность (акселерация)", "Enhance pointer precision (accel)");
    public string ButtonAssignmentsSection => P("Назначение кнопок", "Button Assignments");

    // --- Tab 3: DiagnosticsTabView ---
    public string MiceOnlyFilter => P("Только мыши", "Mice only");
    public string SimulatorFilter => P("Симулятор", "Simulator");
    public string DiagBatteryBtn => P("Заряд", "Battery");
    public string DiagRegistersBtn => P("Регистры", "Registers");
    public string DiagSelectBtn => P("Выбрать", "Select");
    public string DiagDumpBtn => P("Дамп", "Dump");
    public string RawHidPacketHeader => P("Сырой HID-пакет", "Raw HID Packet");
    public string SendBtn => P("Отправить", "Send");
    public string CopyBtn => P("Копировать", "Copy");
    public string ClearBtn => P("Очистить", "Clear");
}
