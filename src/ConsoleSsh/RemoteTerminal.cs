using System.Collections.Concurrent;
using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Owns the SSH side of the terminal. With a PTY it switches the console into raw
/// UTF-8 mode; without one it receives ordinary bytes from sshd and sends UTF-8 output.
/// Input is queued on a reader thread so the render loop never blocks on the network.
/// </summary>
internal sealed class RemoteTerminal : IDisposable
{
    private readonly Stream _input = Console.OpenStandardInput();
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly ConcurrentQueue<byte[]> _inputQueue = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _readerThread;
    private readonly uint _originalInputMode;
    private readonly uint _originalOutputMode;
    private readonly bool _isConsole;
    private readonly int _defaultWidth;
    private readonly int _defaultHeight;
    private bool _disposed;

    public RemoteTerminal()
    {
        _isConsole = NativeMethods.GetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdInputHandle), out _originalInputMode);
        if (_isConsole)
        {
            NativeMethods.GetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle), out _originalOutputMode);
            NativeMethods.SetConsoleCP(65001);
            NativeMethods.SetConsoleOutputCP(65001);
            var inputMode = _originalInputMode;
            inputMode &= ~(NativeMethods.EnableEchoInput |
                           NativeMethods.EnableLineInput |
                           NativeMethods.EnableProcessedInput |
                           NativeMethods.EnableQuickEditMode |
                           NativeMethods.EnableMouseInput);
            inputMode |= NativeMethods.EnableExtendedFlags | NativeMethods.EnableVirtualTerminalInput;
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdInputHandle), inputMode);

            NativeMethods.GetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle), out var outputMode);
            outputMode |= NativeMethods.EnableVirtualTerminalProcessing;
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle), outputMode);
            _defaultWidth = SafeConsoleDimension(static () => Console.WindowWidth, 100);
            _defaultHeight = SafeConsoleDimension(static () => Console.WindowHeight, 32);
        }
        else
        {
            _defaultWidth = ParseDimension("COLUMNS", 100);
            _defaultHeight = ParseDimension("LINES", 32);
        }

        _readerThread = new Thread(ReadInputLoop)
        {
            IsBackground = true,
            Name = "ConsoleSsh.RemoteInput"
        };
        _readerThread.Start();
    }

    public Stream Output => _output;
    public bool IsConsole => _isConsole;
    public bool InputClosed { get; private set; }
    public int Width => _isConsole ? SafeConsoleDimension(static () => Console.WindowWidth, _defaultWidth) : _defaultWidth;
    public int Height => _isConsole ? SafeConsoleDimension(static () => Console.WindowHeight, _defaultHeight) : _defaultHeight;

    public bool TryReadChunk(out byte[] bytes, int timeoutMilliseconds = 0)
    {
        if (_inputQueue.TryDequeue(out bytes!) && bytes.Length > 0)
        {
            return true;
        }

        if (timeoutMilliseconds > 0)
        {
            var deadline = Environment.TickCount64 + timeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                if (_inputQueue.TryDequeue(out bytes!) && bytes.Length > 0)
                {
                    return true;
                }
            }
        }

        bytes = [];
        return false;
    }

    public bool TryReadLine(out string line)
    {
        var buffer = new List<byte>();
        while (_inputQueue.TryDequeue(out var chunk))
        {
            buffer.AddRange(chunk);
        }

        var cr = buffer.IndexOf((byte)'\r');
        var lf = buffer.IndexOf((byte)'\n');
        var separator = cr >= 0 && (lf < 0 || cr < lf) ? cr : lf;
        if (separator < 0)
        {
            if (buffer.Count > 0)
            {
                _inputQueue.Enqueue(buffer.ToArray());
            }

            line = "";
            return false;
        }

        line = Encoding.UTF8.GetString(buffer.Take(separator).ToArray()).TrimEnd('\0');
        var nextIndex = separator + 1;
        if (separator == cr && nextIndex < buffer.Count && buffer[nextIndex] == (byte)'\n')
        {
            nextIndex++;
        }

        var remaining = buffer.Skip(nextIndex).ToArray();
        if (remaining.Length > 0)
        {
            _inputQueue.Enqueue(remaining);
        }

        return true;
    }

    public void Enqueue(byte[] bytes)
    {
        if (bytes.Length > 0)
        {
            _inputQueue.Enqueue(bytes);
        }
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _output.Write(bytes, 0, bytes.Length);
        _output.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        if (_isConsole)
        {
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdInputHandle), _originalInputMode);
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle), _originalOutputMode);
        }

        _cancellation.Dispose();
    }

    private void ReadInputLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var read = _input.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    InputClosed = true;
                    return;
                }

                _inputQueue.Enqueue(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch
        {
            InputClosed = true;
        }
    }

    private static int ParseDimension(string variable, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(value, out var parsed) && parsed is > 10 and < 1000 ? parsed : fallback;
    }

    private static int SafeConsoleDimension(Func<int> getter, int fallback)
    {
        try
        {
            return Math.Clamp(getter(), 10, 1000);
        }
        catch
        {
            return fallback;
        }
    }
}
