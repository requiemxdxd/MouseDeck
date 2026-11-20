using System.IO;

namespace MouseDeck.Win32.Tray;

/// <summary>
/// Thread-safe lightweight rotating log writer (%AppData%\MouseDeck\app.log, max 256 KB).
/// </summary>
public static class AppLogger
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Sync = new();

    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MouseDeck",
        "app.log");

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Error(string area, Exception ex) => Write("ERROR", area, ex.ToString());

    public static void Error(string area, string message) => Write("ERROR", area, message);

    private static void Write(string level, string area, string message)
    {
        lock (Sync)
        {
            try
            {
                var path = LogFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    var backup = path + ".1";
                    File.Move(path, backup, overwrite: true);
                }

                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{area}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Never throw from logging
            }
        }
    }
}
