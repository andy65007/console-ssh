namespace ConsoleSsh;

/// <summary>
/// Captures the visible viewport of an attached console. Coordinates are translated
/// from absolute screen-buffer positions to zero-based viewport cells before they are
/// sent to the remote renderer.
/// </summary>
internal sealed class ConsoleScreenReader : IDisposable
{
    private readonly IntPtr _consoleOutput;
    private bool _disposed;

    public ConsoleScreenReader(IntPtr consoleOutput)
    {
        _consoleOutput = consoleOutput;
    }

    /// <summary>Reads characters and attributes for the current viewport.</summary>
    public bool TryCapture(out ConsoleSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = "";
        if (_disposed || _consoleOutput == IntPtr.Zero || _consoleOutput == new IntPtr(-1))
        {
            error = "控制台输出句柄无效。";
            return false;
        }

        if (!NativeMethods.GetConsoleScreenBufferInfo(_consoleOutput, out var info))
        {
            error = $"读取控制台信息失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。";
            return false;
        }

        var width = info.Window.Right - info.Window.Left + 1;
        var height = info.Window.Bottom - info.Window.Top + 1;
        if (width <= 0 || height <= 0)
        {
            error = "目标控制台窗口尺寸无效。";
            return false;
        }

        width = Math.Min(width, 400);
        height = Math.Min(height, 200);
        var chars = new NativeMethods.CharInfo[width * height];
        var region = info.Window;
        region.Right = (short)(region.Left + width - 1);
        region.Bottom = (short)(region.Top + height - 1);
        if (!NativeMethods.ReadConsoleOutputW(
                _consoleOutput,
                chars,
                new NativeMethods.Coord((short)width, (short)height),
                new NativeMethods.Coord(0, 0),
                ref region))
        {
            error = $"读取控制台画面失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。";
            return false;
        }

        var rows = new string[height];
        var attributes = new byte[height][];
        for (var row = 0; row < height; row++)
        {
            var line = new char[width];
            var attr = new byte[width];
            for (var column = 0; column < width; column++)
            {
                var cell = chars[row * width + column];
                line[column] = cell.UnicodeChar == '\0' ? ' ' : cell.UnicodeChar;
                attr[column] = (byte)(cell.Attributes & 0xFF);
            }

            rows[row] = new string(line);
            attributes[row] = attr;
        }

        var cursorX = Math.Clamp(info.CursorPosition.X - info.Window.Left, 0, width - 1);
        var cursorY = Math.Clamp(info.CursorPosition.Y - info.Window.Top, 0, height - 1);
        snapshot = ConsoleSnapshot.Create(width, height, cursorX, cursorY, rows, attributes);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_consoleOutput != IntPtr.Zero && _consoleOutput != new IntPtr(-1))
        {
            NativeMethods.CloseHandle(_consoleOutput);
        }
    }
}
