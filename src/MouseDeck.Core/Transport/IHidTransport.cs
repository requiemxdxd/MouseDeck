using System.Globalization;
using MouseDeck.Core.Models;

namespace MouseDeck.Core.Transport;

public interface IHidSession : IAsyncDisposable
{
    MouseDeviceInfo Device { get; }
    bool IsOpen { get; }

    /// <summary>Flushes stale queued HID input reports before sending a request (prevents vendor-app collisions).</summary>
    void FlushInputQueue() { }

    Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default);
    Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default);
    Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default);
    Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default);

    /// <summary>
    /// Reads up to <paramref name="maxAttempts"/> input reports within the timeout budget,
    /// skipping unrelated asynchronous reports (e.g. from vendor software or DPI button events) until <paramref name="matcher"/> returns true.
    /// </summary>
    async Task<byte[]?> ReadMatchingInputReportAsync(
        int expectedLength,
        Func<byte[], bool> matcher,
        int timeoutMs = 250,
        int maxAttempts = 4,
        CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 50);
        for (int attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            int remaining = (int)Math.Max(30, deadline - Environment.TickCount64);
            var rx = await ReadInputReportAsync(expectedLength, remaining, ct);
            if (rx is null || rx.Length == 0)
                return null;

            if (matcher(rx))
                return rx;

            if (Environment.TickCount64 >= deadline)
                break;
        }
        return null;
    }

    /// <summary>
    /// Attempts to read a standard HID Power/Battery percentage via OS preparsed value caps (UsagePage 0x0084/0x0085/0x0006).
    /// </summary>
    Task<int?> TryReadStandardHidBatteryAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
}

public interface IHidTransport
{
    IReadOnlyList<MouseDeviceInfo> EnumerateDevices();
    Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo device, CancellationToken ct = default);

    /// <summary>
    /// Optional supplementary enumerator for Bluetooth mice (WinRT PnP / GATT). Default returns empty.
    /// </summary>
    Task<IReadOnlyList<MouseDeviceInfo>> EnumerateBluetoothMiceAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MouseDeviceInfo>>([]);

    /// <summary>
    /// Optional non-intrusive check for running vendor mouse suites (G HUB, Razer Synapse, etc.).
    /// </summary>
    string? DetectRunningVendorSoftware() => null;
}

public static class HidPacketHelper
{
    public static byte[] ParseHexString(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return [];

        var tokens = hex
            .Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ")
            .Replace("-", " ")
            .Replace(":", " ")
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // If the user typed a continuous hex string like "08040000"
        if (tokens.Length == 1 && tokens[0].Length > 2 && tokens[0].Length % 2 == 0)
        {
            var raw = tokens[0];
            var result = new byte[raw.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = byte.Parse(raw.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return result;
        }

        var bytes = new List<byte>(tokens.Length);
        foreach (var token in tokens)
        {
            bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return [.. bytes];
    }

    public static string ToHexString(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        return string.Join(' ', data.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public static byte ComputeChecksum(ReadOnlySpan<byte> data, ChecksumAlgorithm algorithm)
    {
        return algorithm switch
        {
            ChecksumAlgorithm.CompX55 => ComputeCompX55(data),
            ChecksumAlgorithm.Sum8 => ComputeSum8(data),
            ChecksumAlgorithm.Xor8 => ComputeXor8(data),
            _ => 0
        };
    }

    /// <summary>
    /// Standard CompX CX52850 checksum: (0x55 - sum(bytes)) &amp; 0xFF
    /// </summary>
    public static byte ComputeCompX55(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data)
            sum += b;
        return (byte)((0x55 - (sum & 0xFF)) & 0xFF);
    }

    public static byte ComputeSum8(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data)
            sum += b;
        return (byte)(sum & 0xFF);
    }

    public static byte ComputeXor8(ReadOnlySpan<byte> data)
    {
        byte xor = 0;
        foreach (var b in data)
            xor ^= b;
        return xor;
    }

    public static byte[] ResizePacket(ReadOnlySpan<byte> source, int targetLength, byte? reportIdPrefix = null)
    {
        int len = Math.Max(targetLength, source.Length + (reportIdPrefix.HasValue ? 1 : 0));
        var buffer = new byte[len];
        int offset = 0;
        if (reportIdPrefix.HasValue)
        {
            buffer[0] = reportIdPrefix.Value;
            offset = 1;
        }

        var copyLen = Math.Min(source.Length, buffer.Length - offset);
        source[..copyLen].CopyTo(buffer.AsSpan(offset));
        return buffer;
    }
}
