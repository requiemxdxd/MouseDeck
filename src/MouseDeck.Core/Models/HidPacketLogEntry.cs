namespace MouseDeck.Core.Models;

public enum PacketDirection
{
    Tx,
    Rx,
    Info,
    Error
}

public sealed record HidPacketLogEntry(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    string Channel,
    string HexData,
    string Summary)
{
    public string FormattedTime => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string FormattedLine => string.IsNullOrWhiteSpace(HexData)
        ? $"[{FormattedTime}] {Direction.ToString().ToUpperInvariant(),-4} ({Channel}) {Summary}"
        : $"[{FormattedTime}] {Direction.ToString().ToUpperInvariant(),-4} ({Channel}) {HexData}  | {Summary}";
}
