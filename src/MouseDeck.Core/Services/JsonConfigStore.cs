using System.Text.Json;
using System.Text.Json.Serialization;
using MouseDeck.Core.Models;

namespace MouseDeck.Core.Services;

public sealed class JsonConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public string ConfigDirectory { get; }
    public string ConfigFilePath { get; }
    public string ProtocolsDirectory { get; }
    public bool WasCreatedOnLastLoad { get; private set; }

    public JsonConfigStore(string? baseDirectory = null)
    {
        ConfigDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MouseDeck");
        ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
        ProtocolsDirectory = Path.Combine(ConfigDirectory, "protocols");
    }

    public AppConfig Load()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ProtocolsDirectory);

        if (!File.Exists(ConfigFilePath))
        {
            WasCreatedOnLastLoad = true;
            var defaults = AppConfig.CreateDefault();
            Save(defaults);
            return defaults;
        }

        WasCreatedOnLastLoad = false;

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (loaded is null || loaded.Profiles.Count == 0)
            {
                var fallback = AppConfig.CreateDefault();
                Save(fallback);
                return fallback;
            }

            EnsureIntegrity(loaded);
            return loaded;
        }
        catch
        {
            // Keep broken file backup so user never loses custom rules silently
            try
            {
                var backup = ConfigFilePath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(ConfigFilePath, backup, overwrite: true);
            }
            catch { }

            var defaults = AppConfig.CreateDefault();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(AppConfig config)
    {
        _ioLock.Wait();
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, SerializerOptions);
            var tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, SerializerOptions);
            var tempPath = ConfigFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static void EnsureIntegrity(AppConfig cfg)
    {
        cfg.DeviceCache ??= new Dictionary<string, CachedDeviceBattery>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cfg.DeviceCache.Values)
        {
            entry.History ??= [];
        }
        cfg.ProcessRules ??= [];
        cfg.Hotkeys ??= [];

        if (!cfg.Profiles.Any(p => p.Id == cfg.ActiveProfileId))
            cfg.ActiveProfileId = cfg.Profiles[0].Id;

        if (!cfg.Profiles.Any(p => p.Id == cfg.DefaultDesktopProfileId))
            cfg.DefaultDesktopProfileId = cfg.Profiles[0].Id;

        foreach (var profile in cfg.Profiles)
        {
            if (profile.DpiStages.Count == 0)
            {
                profile.DpiStages.Add(new DpiStage { Dpi = 800, DpiY = 800, ColorHex = "#FF38BDF8" });
            }
            foreach (var st in profile.DpiStages)
            {
                if (st.DpiY <= 0) st.DpiY = st.Dpi;
            }
            profile.ButtonMappings ??= MouseProfile.CreateDefaultButtonMappings();
            profile.ActiveDpiStageIndex = Math.Clamp(profile.ActiveDpiStageIndex, 0, profile.DpiStages.Count - 1);
        }
    }
}
