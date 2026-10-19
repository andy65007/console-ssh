using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Renders console snapshots with ANSI cursor addressing and only rewrites changed
/// rows. Alternate-screen mode isolates the mirrored console from the SSH scrollback.
/// </summary>
internal sealed class AnsiRenderer
{
    private readonly Stream _output;
    private string[] _previousRows = [];
    private string _previousStatus = "";
    private int _previousWidth = -1;
    private int _previousHeight = -1;
    private int _previousCursorX = -1;
    private int _previousCursorY = -1;
    private bool _attached;

    public AnsiRenderer(Stream output)
    {
        _output = output;
    }

    public void EnterAttachedMode()
    {
        Write("\u001b[?1049h\u001b[?25l\u001b[2J");
        _attached = true;
        Invalidate();
    }

    public void LeaveAttachedMode()
    {
        if (!_attached)
        {
            return;
        }

        Write("\u001b[0m\u001b[?25h\u001b[?1049l");
        _attached = false;
        Invalidate();
    }

    public void Invalidate()
    {
        _previousRows = [];
        _previousStatus = "";
        _previousWidth = -1;
        _previousHeight = -1;
        _previousCursorX = -1;
        _previousCursorY = -1;
    }

    /// <summary>
    /// Draws the viewport plus a one-line status strip. Full redraw is reserved for
    /// resize/force events; normal frames use row diffs to keep latency and bandwidth low.
    /// </summary>
    public void RenderAttached(ConsoleSnapshot snapshot, int terminalWidth, int terminalHeight, string status, bool force = false)
    {
        if (!_attached)
        {
            EnterAttachedMode();
        }

        terminalWidth = Math.Max(20, terminalWidth);
        terminalHeight = Math.Max(6, terminalHeight);
        var usableWidth = Math.Max(1, terminalWidth - 1);
        var screenRows = Math.Max(1, terminalHeight - 1);

        if (force || terminalWidth != _previousWidth || terminalHeight != _previousHeight)
        {
            Write("\u001b[2J");
            Invalidate();
            _previousWidth = terminalWidth;
            _previousHeight = terminalHeight;
        }

        var rows = new string[screenRows];
        for (var row = 0; row < screenRows; row++)
        {
            rows[row] = row < snapshot.Height
                ? BuildColorizedLine(snapshot.Rows[row], snapshot.Attributes[row], usableWidth)
                : "";

            if (row >= _previousRows.Length || !string.Equals(rows[row], _previousRows[row], StringComparison.Ordinal))
            {
                Write($"\u001b[{row + 1};1H\u001b[2K");
                Write(rows[row]);
            }
        }

        var statusText = Truncate(status, usableWidth);
        if (!string.Equals(statusText, _previousStatus, StringComparison.Ordinal))
        {
            Write($"\u001b[{terminalHeight};1H\u001b[2K\u001b[7m {statusText}\u001b[0m");
            _previousStatus = statusText;
        }

        var cursorX = Math.Clamp(snapshot.CursorX, 0, Math.Max(0, Math.Min(usableWidth - 1, snapshot.Width - 1)));
        var cursorY = Math.Clamp(snapshot.CursorY, 0, Math.Max(0, Math.Min(screenRows - 1, snapshot.Height - 1)));
        if (cursorX != _previousCursorX || cursorY != _previousCursorY)
        {
            Write($"\u001b[{cursorY + 1};{cursorX + 1}H\u001b[?25h");
            _previousCursorX = cursorX;
            _previousCursorY = cursorY;
        }

        _previousRows = rows;
        _output.Flush();
    }

    public void WriteMenu(string text)
    {
        Write("\u001b[0m\u001b[2J\u001b[H");
        Write(text);
        _output.Flush();
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _output.Write(bytes, 0, bytes.Length);
    }

    private static string BuildColorizedLine(string text, byte[] attributes, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        var length = Math.Min(width, text.Length);
        var builder = new StringBuilder(length + 32);
        byte currentAttribute = 0xFF;
        for (var index = 0; index < length; index++)
        {
            var attribute = index < attributes.Length ? attributes[index] : (byte)0x07;
            if (attribute != currentAttribute)
            {
                builder.Append(BuildSgr(attribute));
                currentAttribute = attribute;
            }

            var ch = text[index];
            builder.Append(ch is '\0' or '\r' or '\n' ? ' ' : ch);
        }

        if (currentAttribute != 0x07)
        {
            builder.Append("\u001b[0m");
        }

        return builder.ToString();
    }

    /// <summary>Maps Windows console color nibbles to ANSI SGR foreground/background codes.</summary>
    private static string BuildSgr(byte attribute)
    {
        var foreground = attribute & 0x0F;
        var background = (attribute >> 4) & 0x0F;
        var foregroundBright = (foreground & 0x08) != 0;
        var backgroundBright = (background & 0x08) != 0;
        var foregroundColor = foreground & 0x07;
        var backgroundColor = background & 0x07;
        var fg = foregroundBright ? 90 + foregroundColor : 30 + foregroundColor;
        var bg = backgroundBright ? 100 + backgroundColor : 40 + backgroundColor;
        return $"\u001b[0;{fg};{bg}m";
    }

    private static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + "…";
    }
}
