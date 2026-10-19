using System.Diagnostics;
using System.Text;

namespace ConsoleSsh;

/// <summary>One classic console window and the owning process ID used by AttachConsole.</summary>
internal sealed record ConsoleWindowInfo(uint ProcessId, string ProcessName, string Title, IntPtr WindowHandle);

/// <summary>
/// Enumerates only ConsoleWindowClass windows. Windows Terminal tabs use ConPTY and
/// do not expose a classic console buffer, so they are counted for a user-facing hint
/// but never offered as attach targets.
/// </summary>
internal static class ConsoleWindowEnumerator
{
    private const string ClassicConsoleClass = "ConsoleWindowClass";
    private const string WindowsTerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string PseudoConsoleClass = "PseudoConsoleWindow";

    internal static (List<ConsoleWindowInfo> Windows, int WindowsTerminalCount) Enumerate()
    {
        var windows = new Dictionary<uint, ConsoleWindowInfo>();
        var terminalCount = 0;
        var currentSession = Process.GetCurrentProcess().SessionId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                var className = GetClassName(hWnd);
                if (className.Equals(WindowsTerminalClass, StringComparison.Ordinal) ||
                    className.Equals(PseudoConsoleClass, StringComparison.Ordinal))
                {
                    terminalCount++;
                    return true;
                }

                if (!className.Equals(ClassicConsoleClass, StringComparison.Ordinal))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                if (processId == 0 || !windows.ContainsKey(processId))
                {
                    Process process;
                    try
                    {
                        process = Process.GetProcessById((int)processId);
                    }
                    catch
                    {
                        return true;
                    }

                    using (process)
                    {
                        if (process.SessionId != currentSession)
                        {
                            return true;
                        }

                        var title = GetWindowText(hWnd);
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            title = $"{process.ProcessName} ({processId})";
                        }

                        windows[processId] = new ConsoleWindowInfo(
                            processId,
                            process.ProcessName,
                            title,
                            hWnd);
                    }
                }
            }
            catch
            {
                // A window can disappear while the list is being built.
            }

            return true;
        }, IntPtr.Zero);

        var result = windows.Values
            .OrderByDescending(static w => w.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static w => w.ProcessId)
            .ToList();

        return (result, terminalCount);
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassNameW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var length = Math.Max(0, NativeMethods.GetWindowTextLengthW(hWnd));
        var buffer = new StringBuilder(Math.Max(256, length + 1));
        NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
