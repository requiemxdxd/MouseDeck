using MouseDeck.Core.Models;

namespace MouseDeck.Core.Services;

/// <summary>
/// Event-driven profile switcher driven by Win32 SetWinEventHook foreground process changes
/// and global hotkeys. Includes debounce to avoid rapid HID EEPROM writes on Alt+Tab.
/// </summary>
public sealed class ProfileAutomationService : IDisposable
{
    private readonly DeviceCoordinator _coordinator;
    private readonly TimeSpan _debounceDelay;
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    private string? _lastEvaluatedProcess;

    public ProfileAutomationService(DeviceCoordinator coordinator, TimeSpan? debounceDelay = null)
    {
        _coordinator = coordinator;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(450);
    }

    public event Action<string, ProfileSwitchSource>? ProfileAutoSwitched;

    public void OnForegroundProcessChanged(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        _coordinator.UpdateActiveForegroundProcess(normalized);

        if (!_coordinator.IsWriteUnlocked || !_coordinator.Config.AutoSwitchEnabled || _coordinator.Config.ManualProfileOverride)
            return;

        if (string.Equals(normalized, _lastEvaluatedProcess, StringComparison.OrdinalIgnoreCase))
            return;

        _lastEvaluatedProcess = normalized;

        CancellationToken token;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = EvaluateAfterDebounceAsync(normalized, token);
    }

    private async Task EvaluateAfterDebounceAsync(string? normalizedProcess, CancellationToken ct)
    {
        try
        {
            if (_debounceDelay > TimeSpan.Zero)
                await Task.Delay(_debounceDelay, ct);

            await EvaluateNowAsync(normalizedProcess, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer window focus change
        }
    }

    public async Task EvaluateNowAsync(string? processName, CancellationToken ct = default)
    {
        var cfg = _coordinator.Config;
        if (!_coordinator.IsWriteUnlocked || !cfg.AutoSwitchEnabled || cfg.ManualProfileOverride)
            return;

        var normalized = NormalizeProcessName(processName);
        string targetProfileId = ResolveTargetProfileId(cfg, normalized);

        if (cfg.ActiveProfileId == targetProfileId)
            return;

        await _coordinator.SelectProfileAsync(targetProfileId, ProfileSwitchSource.AutoProcess, ct);
        var targetName = cfg.Profiles.FirstOrDefault(p => p.Id == targetProfileId)?.Name ?? targetProfileId;
        ProfileAutoSwitched?.Invoke(targetName, ProfileSwitchSource.AutoProcess);
    }

    public static string ResolveTargetProfileId(AppConfig cfg, string? normalizedProcessName)
    {
        if (!string.IsNullOrWhiteSpace(normalizedProcessName))
        {
            var matched = cfg.ProcessRules.FirstOrDefault(r =>
                r.Enabled &&
                string.Equals(NormalizeProcessName(r.ProcessName), normalizedProcessName, StringComparison.OrdinalIgnoreCase));

            if (matched is not null && cfg.Profiles.Any(p => p.Id == matched.ProfileId))
                return matched.ProfileId;
        }

        return cfg.Profiles.Any(p => p.Id == cfg.DefaultDesktopProfileId)
            ? cfg.DefaultDesktopProfileId
            : cfg.Profiles[0].Id;
    }

    public async Task HandleHotkeyActionAsync(string action, CancellationToken ct = default)
    {
        var cfg = _coordinator.Config;
        if (cfg.Profiles.Count == 0) return;

        string? nextId = null;
        if (action.Equals("CycleProfile", StringComparison.OrdinalIgnoreCase))
        {
            int idx = cfg.Profiles.FindIndex(p => p.Id == cfg.ActiveProfileId);
            int nextIdx = (idx + 1) % cfg.Profiles.Count;
            nextId = cfg.Profiles[nextIdx].Id;
        }
        else if (action.Equals("ToggleEcoGaming", StringComparison.OrdinalIgnoreCase))
        {
            string ecoId = cfg.DefaultDesktopProfileId;
            string gameId = cfg.Profiles.Any(p => p.Id == cfg.GamingQuickToggleProfileId)
                ? cfg.GamingQuickToggleProfileId
                : cfg.Profiles.Last().Id;

            nextId = cfg.ActiveProfileId == ecoId ? gameId : ecoId;
        }
        else if (action.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase))
        {
            var requestedId = action["Profile:".Length..];
            if (cfg.Profiles.Any(p => p.Id == requestedId))
                nextId = requestedId;
        }

        if (nextId is null) return;

        await _coordinator.SelectProfileAsync(nextId, ProfileSwitchSource.Hotkey, ct);
        var name = cfg.Profiles.FirstOrDefault(p => p.Id == nextId)?.Name ?? nextId;
        ProfileAutoSwitched?.Invoke(name, ProfileSwitchSource.Hotkey);
    }

    public static string? NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var file = Path.GetFileName(name.Trim());
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file += ".exe";
        return file.ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
