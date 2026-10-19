using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Minimal file logger used because the service has no attached console. Logging
/// failures are swallowed so disk/permission issues cannot kill an SSH session.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _alsoConsole;

    public static void Initialize(string path, bool alsoConsole = false)
    {
        lock (Gate)
        {
            _path = path;
            _alsoConsole = alsoConsole;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch
            {
                // Logging must never take down the service.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [pid {Environment.ProcessId}] {message}";
        lock (Gate)
        {
            if (_alsoConsole)
            {
                Console.Error.WriteLine(line);
            }

            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // Never throw from logging.
            }
        }
    }
}
