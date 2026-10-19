using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Immutable screen viewport sent over the worker pipe. Key is a compact fingerprint
/// used to suppress unchanged frames.
/// </summary>
internal sealed class ConsoleSnapshot
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public string[] Rows { get; init; } = [];
    public byte[][] Attributes { get; init; } = [];
    public string Key { get; init; } = "";

    public static ConsoleSnapshot Create(
        int width,
        int height,
        int cursorX,
        int cursorY,
        IReadOnlyList<string> rows,
        IReadOnlyList<byte[]> attributes)
    {
        var key = new StringBuilder(rows.Count * (width + 8));
        key.Append(width).Append('x').Append(height).Append('@').Append(cursorX).Append(',').Append(cursorY);
        for (var row = 0; row < rows.Count; row++)
        {
            key.Append('\n').Append(rows[row]).Append('\u0001');
            key.Append(Convert.ToHexString(attributes[row]));
        }

        return new ConsoleSnapshot
        {
            Width = width,
            Height = height,
            CursorX = cursorX,
            CursorY = cursorY,
            Rows = rows.ToArray(),
            Attributes = attributes.Select(static a => a.ToArray()).ToArray(),
            Key = key.ToString()
        };
    }
}
