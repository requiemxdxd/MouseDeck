using MouseDeck.Core.Models;

namespace MouseDeck.Core.Protocols;

public sealed record ProtocolMatch(MouseDeviceInfo Device, IMouseProtocol Protocol, int Score);

public sealed class ProtocolRegistry
{
    private readonly List<IMouseProtocol> _protocols = [];

    public ProtocolRegistry(IEnumerable<IMouseProtocol> builtInProtocols, string? customProtocolsDir = null)
    {
        _protocols.AddRange(builtInProtocols);
        if (!string.IsNullOrWhiteSpace(customProtocolsDir))
        {
            _protocols.AddRange(JsonTemplateProtocol.LoadFromDirectory(customProtocolsDir));
        }
    }

    public IReadOnlyList<IMouseProtocol> Protocols => _protocols;

    public void Register(IMouseProtocol protocol)
    {
        _protocols.RemoveAll(p => p.ProtocolId.Equals(protocol.ProtocolId, StringComparison.OrdinalIgnoreCase));
        _protocols.Add(protocol);
    }

    public IMouseProtocol? FindBestProtocol(MouseDeviceInfo device)
    {
        IMouseProtocol? best = null;
        int bestScore = 0;

        foreach (var protocol in _protocols)
        {
            int score = protocol.MatchScore(device);
            if (score > bestScore || (score > 0 && score == bestScore && protocol.Priority > (best?.Priority ?? 0)))
            {
                bestScore = score;
                best = protocol;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns all unique physical mice matched across USB HID and Bluetooth, deduplicated by DeviceKey
    /// so each mouse appears once under its highest-capability protocol (e.g., CompX instead of Generic HID).
    /// </summary>
    public IReadOnlyList<ProtocolMatch> FindAllMatchedMice(IEnumerable<MouseDeviceInfo> devices, string? preferredDevicePath = null)
    {
        var rawMatches = new List<ProtocolMatch>();
        foreach (var dev in devices)
        {
            var proto = FindBestProtocol(dev);
            if (proto is null) continue;

            int score = proto.MatchScore(dev);
            if (!string.IsNullOrWhiteSpace(preferredDevicePath) &&
                dev.DevicePath.Equals(preferredDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
            }

            // Real hardware always outranks virtual demo mouse unless virtual is explicitly pinned
            if (!dev.IsVirtual)
                score += 100;

            rawMatches.Add(new ProtocolMatch(dev, proto, score));
        }

        // Deduplicate sub-collections of the same physical mouse (same VID:PID or BT key)
        var deduped = new List<ProtocolMatch>();
        foreach (var group in rawMatches.GroupBy(m => m.Device.IsVirtual ? "VIRTUAL" : m.Device.DeviceKey, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.Protocol.Priority)
                .First();
            deduped.Add(winner);
        }

        return deduped
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Protocol.Priority)
            .ToList();
    }

    public ProtocolMatch? SelectPrimaryDevice(IEnumerable<MouseDeviceInfo> devices, string? preferredDevicePath = null)
    {
        return FindAllMatchedMice(devices, preferredDevicePath).FirstOrDefault();
    }
}
