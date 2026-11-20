using MouseDeck.Core.Models;

namespace MouseDeck.Core.Services;

/// <summary>
/// Thread-safe live localization switch (Russian / English) shared across Core, Win32, and App layers.
/// </summary>
public static class AppLocalization
{
    private static volatile AppLanguage _currentLanguage = AppLanguage.English;

    public static AppLanguage CurrentLanguage => _currentLanguage;
    public static bool IsRussian => _currentLanguage == AppLanguage.Russian;

    public static event Action<AppLanguage>? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (_currentLanguage == language)
            return;

        _currentLanguage = language;
        LanguageChanged?.Invoke(language);
    }

    public static string Pick(string ru, string en) =>
        _currentLanguage == AppLanguage.Russian ? ru : en;
}
