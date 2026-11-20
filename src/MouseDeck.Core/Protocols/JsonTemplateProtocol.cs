using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

public sealed class CustomProtocolDefinition
{
    public string ProtocolId { get; set; } = "custom-json";
    public string DisplayName { get; set; } = "Custom JSON Mouse";
    public string VendorIdHex { get; set; } = "0x1D57";
    public string? ProductIdHex { get; set; }
    public string? UsagePageHex { get; set; } = "0xFF02";
    public bool UseFeatureReports { get; set; } = true;
    public int ReportLength { get; set; } = 17;
    public byte ReportId { get; set; } = 0x08;
    public ChecksumAlgorithm Checksum { get; set; } = ChecksumAlgorithm.CompX55;

    public string BatteryQueryHex { get; set; } = "08 04 00 00 00 00";
    public int BatteryPercentOffset { get; set; } = 6;
    public int ChargingFlagOffset { get; set; } = 7;
    public int? VoltageHighOffset { get; set; } = 8;
    public int? VoltageLowOffset { get; set; } = 9;

    public List<int> SupportedPollingRatesHz { get; set; } = [125, 250, 500, 1000];
    public int MaxDpi { get; set; } = 26000;
}

/// <summary>
/// Data-driven protocol allowing users to add new Chinese mouse VID/PID battery &amp; packet definitions
/// via JSON files inside %AppData%\MouseDeck\protocols\ without recompiling.
/// </summary>
public sealed class JsonTemplateProtocol : IMouseProtocol
{
    private readonly CustomProtocolDefinition _def;
    private readonly ushort _vid;
    private readonly ushort? _pid;
    private readonly ushort? _usagePage;

    public JsonTemplateProtocol(CustomProtocolDefinition definition)
    {
        _def = definition;
        _vid = ParseHexU16(definition.VendorIdHex);
        _pid = string.IsNullOrWhiteSpace(definition.ProductIdHex) ? null : ParseHexU16(definition.ProductIdHex);
        _usagePage = string.IsNullOrWhiteSpace(definition.UsagePageHex) ? null : ParseHexU16(definition.UsagePageHex);

        SupportedDevices = [new DeviceMatchRule(_vid, _pid, _usagePage, FriendlyModelName: definition.DisplayName)];
        Capabilities = new MouseCapabilities
        {
            MaxDpi = Math.Clamp(definition.MaxDpi, 1600, 36000),
            SupportedPollingRatesHz = definition.SupportedPollingRatesHz.Count > 0
                ? definition.SupportedPollingRatesHz
                : [125, 250, 500, 1000],
            IsWriteOnlyConfig = true
        };
    }

    public string ProtocolId => _def.ProtocolId;
    public string DisplayName => $"{_def.DisplayName} (JSON)";
    public int Priority => 100; // Custom user JSON profiles override built-in fallbacks
    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; }
    public MouseCapabilities Capabilities { get; }

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || !device.CanOpenReadWrite) return 0;
        if (device.VendorId != _vid) return 0;
        if (_pid.HasValue && device.ProductId != _pid.Value) return 0;
        if (_usagePage.HasValue && device.UsagePage != _usagePage.Value) return 0;
        return _pid.HasValue ? 140 : 110;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device) => MouseTransportKind.Wireless24G;

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var reading = await ReadBatteryAsync(session, log, ct);
        return reading is { IsMouseAwake: true };
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var rawBytes = HidPacketHelper.ParseHexString(_def.BatteryQueryHex);
        var packet = HidPacketHelper.ResizePacket(rawBytes, Math.Max(_def.ReportLength, 1));
        if (_def.Checksum != ChecksumAlgorithm.None && packet.Length >= 2)
        {
            packet[^1] = HidPacketHelper.ComputeChecksum(packet.AsSpan(0, packet.Length - 1), _def.Checksum);
        }

        string ch = _def.UseFeatureReports ? "Feature" : "Interrupt";
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, ch, HidPacketHelper.ToHexString(packet), "JSON Battery Query"));

        byte[]? rx;
        if (_def.UseFeatureReports)
        {
            if (!await session.SetFeatureReportAsync(packet, ct)) return null;
            await Task.Delay(20, ct);
            rx = await session.GetFeatureReportAsync(_def.ReportId, packet.Length, ct);
        }
        else
        {
            if (!await session.WriteOutputReportAsync(packet, 250, ct)) return null;
            rx = await session.ReadInputReportAsync(Math.Max(packet.Length, session.Device.InputReportLength), 250, ct);
        }

        if (rx is null || rx.Length <= _def.BatteryPercentOffset)
            return null;

        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, ch, HidPacketHelper.ToHexString(rx), "JSON Battery Reply"));

        int pct = rx[_def.BatteryPercentOffset];
        bool chg = _def.ChargingFlagOffset >= 0 && _def.ChargingFlagOffset < rx.Length && rx[_def.ChargingFlagOffset] != 0;
        int? mv = null;
        if (_def.VoltageHighOffset is int hi && _def.VoltageLowOffset is int lo && hi < rx.Length && lo < rx.Length)
        {
            int rawMv = (rx[hi] << 8) | rx[lo];
            if (rawMv is >= 2800 and <= 4500) mv = rawMv;
        }

        if (pct == 0 && !chg && mv is null)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        return new BatteryReading(Math.Clamp(pct, 1, 100), chg, mv, IsMouseAwake: true);
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default) =>
        Task.FromResult<MouseProfile?>(null);

    public Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
    {
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Info, "JSON", string.Empty,
            $"Profile '{profile.Name}' saved locally (JSON template '{_def.ProtocolId}' is configured for battery monitoring)."));
        return Task.FromResult(true);
    }

    public static IReadOnlyList<JsonTemplateProtocol> LoadFromDirectory(string directoryPath)
    {
        var list = new List<JsonTemplateProtocol>();
        try
        {
            Directory.CreateDirectory(directoryPath);
            EnsureSampleFile(directoryPath);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var def = JsonSerializer.Deserialize<CustomProtocolDefinition>(json, options);
                    if (def is not null && !string.IsNullOrWhiteSpace(def.VendorIdHex))
                    {
                        list.Add(new JsonTemplateProtocol(def));
                    }
                }
                catch
                {
                    // Skip malformed user template gracefully
                }
            }
        }
        catch
        {
            // Ignore directory errors
        }
        return list;
    }

    private static void EnsureSampleFile(string directoryPath)
    {
        var samplePath = Path.Combine(directoryPath, "_example_custom_mouse.json.sample");
        if (File.Exists(samplePath)) return;

        var sample = new CustomProtocolDefinition
        {
            ProtocolId = "my-custom-paw3395",
            DisplayName = "My Custom PAW3395",
            VendorIdHex = "0x1D57",
            ProductIdHex = "0xFA60",
            UsagePageHex = "0xFF02",
            UseFeatureReports = true,
            ReportLength = 17,
            ReportId = 0x08,
            Checksum = ChecksumAlgorithm.CompX55,
            BatteryQueryHex = "08 04 00 00 00 00",
            BatteryPercentOffset = 6,
            ChargingFlagOffset = 7,
            VoltageHighOffset = 8,
            VoltageLowOffset = 9,
            SupportedPollingRatesHz = [125, 250, 500, 1000, 2000, 4000],
            MaxDpi = 26000
        };

        var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(samplePath, json);
    }

    private static ushort ParseHexU16(string text)
    {
        var cleaned = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return ushort.Parse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
