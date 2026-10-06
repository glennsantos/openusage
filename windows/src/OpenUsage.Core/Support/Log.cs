namespace OpenUsage.Core.Support;

/// Append-only log file at %LOCALAPPDATA%\OpenUsage\logs\openusage.log. Never logs tokens.
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUsage", "logs", "openusage.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e == null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line);
            }
            catch (IOException)
            {
                // The log is the failure channel itself; there is nowhere further to report to.
            }
        }
    }
}
