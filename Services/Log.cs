using System.IO;

namespace ClaudeUsageOverlay;

/// <summary>
/// Tiny append-only log. The overlay never interrupts the user with an error dialog, so
/// anything that goes wrong has to be recoverable here instead.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(Services.AppSettings.Directory, "overlay.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Services.AppSettings.Directory);

                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
