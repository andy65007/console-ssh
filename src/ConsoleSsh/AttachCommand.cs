using System.Diagnostics;
using System.Text;

namespace ConsoleSsh;

/// <summary>
/// SSH-side user interface. It lists classic console windows, then starts one worker
/// per selected PID. The worker is kept separate because a process can have only one
/// attached console while the SSH endpoint needs its own PTY.
/// </summary>
internal static class AttachCommand
{
    private const byte SnapshotFrame = 1;
    private const byte ErrorFrame = 2;
    private const byte ExitFrame = 3;
    private const byte InputFrame = (byte)'I';
    private const byte QuitFrame = (byte)'Q';

    /// <summary>Runs the window picker and re-enters it after a worker is detached.</summary>
    public static int Run(AppConfig config, string? dataDirectory)
    {
        var logDirectory = AppPaths.UserLogDirectory(dataDirectory);
        Log.Initialize(Path.Combine(logDirectory, "attach.log"));
        Log.Info($"Attach endpoint starting as {Environment.UserName}, session {Process.GetCurrentProcess().SessionId}.");

        using var terminal = new RemoteTerminal();
        var renderer = new AnsiRenderer(terminal.Output);
        var selectedIndex = 0;
        string? notice = null;
        var numberBuffer = "";
        var menuDirty = true;
        List<ConsoleWindowInfo> windows = [];
        var windowsTerminalCount = 0;

        while (true)
        {
            if (menuDirty)
            {
                (windows, windowsTerminalCount) = ConsoleWindowEnumerator.Enumerate();
                if (windows.Count == 0)
                {
                    selectedIndex = 0;
                }
                else
                {
                    selectedIndex = Math.Clamp(selectedIndex, 0, windows.Count - 1);
                }

                renderer.WriteMenu(BuildMenu(
                    windows,
                    selectedIndex,
                    windowsTerminalCount,
                    notice,
                    numberBuffer,
                    terminal.Width,
                    terminal.IsConsole));
                notice = null;
                menuDirty = false;
            }

            var key = terminal.IsConsole
                ? ReadRawMenuKey(terminal)
                : ReadCookedMenuLine(terminal);

            if (!key.HasValue)
            {
                if (terminal.InputClosed)
                {
                    renderer.LeaveAttachedMode();
                    return 0;
                }

                continue;
            }

            switch (key.Value.Kind)
            {
                case MenuKeyKind.None:
                    continue;
                case MenuKeyKind.Quit:
                    renderer.LeaveAttachedMode();
                    return 0;
                case MenuKeyKind.Refresh:
                    numberBuffer = "";
                    menuDirty = true;
                    continue;
                case MenuKeyKind.Up:
                    if (windows.Count > 0)
                    {
                        selectedIndex = (selectedIndex - 1 + windows.Count) % windows.Count;
                    }

                    numberBuffer = "";
                    menuDirty = true;
                    continue;
                case MenuKeyKind.Down:
                    if (windows.Count > 0)
                    {
                        selectedIndex = (selectedIndex + 1) % windows.Count;
                    }

                    numberBuffer = "";
                    menuDirty = true;
                    continue;
                case MenuKeyKind.Digit:
                    numberBuffer += key.Value.Character;
                    if (int.TryParse(numberBuffer, out var number) &&
                        number >= 1 &&
                        number <= windows.Count)
                    {
                        selectedIndex = number - 1;
                    }

                    menuDirty = true;
                    continue;
                case MenuKeyKind.Line:
                    var line = key.Value.Line.Trim();
                    if (line.Equals("q", StringComparison.OrdinalIgnoreCase))
                    {
                        renderer.LeaveAttachedMode();
                        return 0;
                    }

                    if (line.Equals("r", StringComparison.OrdinalIgnoreCase) || line.Length == 0)
                    {
                        menuDirty = true;
                        continue;
                    }

                    if (!int.TryParse(line, out var lineNumber) ||
                        lineNumber < 1 ||
                        lineNumber > windows.Count)
                    {
                        notice = "编号无效，请输入列表中的编号。";
                        menuDirty = true;
                        continue;
                    }

                    selectedIndex = lineNumber - 1;
                    goto case MenuKeyKind.Enter;
                case MenuKeyKind.Enter:
                    if (numberBuffer.Length > 0)
                    {
                        if (!int.TryParse(numberBuffer, out var enteredNumber) ||
                            enteredNumber < 1 ||
                            enteredNumber > windows.Count)
                        {
                            notice = "编号无效，请输入列表中的编号。";
                            numberBuffer = "";
                            menuDirty = true;
                            continue;
                        }
                    }

                    break;
            }

            if (windows.Count == 0)
            {
                notice = "当前没有可附着的经典控制台窗口。请先在本机打开 cmd.exe，然后按 R 刷新。";
                menuDirty = true;
                continue;
            }

            var selected = windows[selectedIndex];
            numberBuffer = "";
            notice = RunAttached(selected, config, dataDirectory, terminal, renderer);
            renderer.LeaveAttachedMode();
            numberBuffer = "";
            menuDirty = true;
        }
    }

    /// <summary>
    /// Owns the worker IPC channel and terminal loop while one console is attached.
    /// Input is forwarded byte-for-byte; snapshots are rendered from the worker thread.
    /// </summary>
    private static string? RunAttached(
        ConsoleWindowInfo window,
        AppConfig config,
        string? dataDirectory,
        RemoteTerminal terminal,
        AnsiRenderer renderer)
    {
        var logDirectory = AppPaths.UserLogDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        var workerLog = Path.Combine(logDirectory, $"worker-{window.ProcessId}.log");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AppPaths.CurrentExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("worker");
        process.StartInfo.ArgumentList.Add("--pid");
        process.StartInfo.ArgumentList.Add(window.ProcessId.ToString());
        process.StartInfo.ArgumentList.Add("--interval");
        process.StartInfo.ArgumentList.Add(config.PollIntervalMs.ToString());
        process.StartInfo.ArgumentList.Add("--log");
        process.StartInfo.ArgumentList.Add(workerLog);

        try
        {
            if (!process.Start())
            {
                return "无法启动控制台工作进程。";
            }
        }
        catch (Exception exception)
        {
            Log.Error("Failed to start worker process.", exception);
            return $"无法启动控制台工作进程：{exception.Message}";
        }

        var writer = new BinaryWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: true);
        var reader = new BinaryReader(process.StandardOutput.BaseStream, new UTF8Encoding(false), leaveOpen: true);
        var stateGate = new object();
        ConsoleSnapshot? latest = null;
        string? workerError = null;
        var workerExited = false;
        var redraw = true;

        var stderrThread = new Thread(() =>
        {
            try
            {
                var message = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    Log.Warn("Worker stderr: " + message.Trim());
                }
            }
            catch
            {
                // Ignore stderr shutdown races.
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleSsh.WorkerStderr"
        };
        stderrThread.Start();

        var readerThread = new Thread(() =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var frame = reader.ReadByte();
                    if (frame == SnapshotFrame)
                    {
                        var snapshot = ReadSnapshot(reader);
                        lock (stateGate)
                        {
                            latest = snapshot;
                        }
                    }
                    else if (frame == ErrorFrame)
                    {
                        var message = reader.ReadString();
                        lock (stateGate)
                        {
                            workerError = message;
                        }
                    }
                    else if (frame == ExitFrame)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Pipe closes when the remote connection or worker exits.
            }
            finally
            {
                lock (stateGate)
                {
                    workerExited = true;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleSsh.WorkerReader"
        };
        readerThread.Start();

        renderer.EnterAttachedMode();
        var baseStatus = terminal.IsConsole
            ? $"PID {window.ProcessId} {window.ProcessName} | Ctrl+] 窗口列表"
            : $"PID {window.ProcessId} {window.ProcessName} | 无PTY | Ctrl+] 窗口列表";
        var inputPreview = new StringBuilder();
        var lastSubmitted = "";
        var lastSubmittedAt = DateTime.MinValue;
        string? result = null;
        var lastWidth = terminal.Width;
        var lastHeight = terminal.Height;
        var attachNoticeShown = false;

        try
        {
            while (true)
            {
                ConsoleSnapshot? current;
                string? error;
                bool exited;
                lock (stateGate)
                {
                    current = latest;
                    error = workerError;
                    exited = workerExited;
                }

                if (current is not null)
                {
                    var width = terminal.Width;
                    var height = terminal.Height;
                    var status = BuildAttachedStatus(
                        baseStatus,
                        inputPreview,
                        lastSubmitted,
                        lastSubmittedAt,
                        width);
                    if (width != lastWidth || height != lastHeight)
                    {
                        redraw = true;
                        lastWidth = width;
                        lastHeight = height;
                    }

                    renderer.RenderAttached(current, width, height, status, redraw);
                    redraw = false;
                }
                else if (!attachNoticeShown)
                {
                    renderer.Write("\u001b[H\u001b[2J\u001b[0m正在附着到目标控制台…\r\n");
                    attachNoticeShown = true;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    result = error;
                    break;
                }

                if (exited)
                {
                    result = "目标控制台已关闭，或工作进程已退出。";
                    break;
                }

                if (terminal.InputClosed)
                {
                    break;
                }

                // Both PTY and pipe clients use the same forwarding path. In PTY mode
                // the target console echoes immediately; in raw pipe mode this is also
                // what makes no-PTY clients visible instead of waiting for Enter.
                var detach = false;
                while (terminal.TryReadChunk(out var chunk))
                {
                    var index = Array.IndexOf(chunk, (byte)0x1D);
                    if (index >= 0)
                    {
                        if (index > 0)
                        {
                            var prefix = chunk.AsSpan(0, index).ToArray();
                            UpdateInputPreview(
                                prefix,
                                inputPreview,
                                ref lastSubmitted,
                                ref lastSubmittedAt);
                            SendInput(writer, prefix);
                        }

                        detach = true;
                        break;
                    }

                    UpdateInputPreview(
                        chunk,
                        inputPreview,
                        ref lastSubmitted,
                        ref lastSubmittedAt);
                    SendInput(writer, chunk);
                }

                if (detach)
                {
                    break;
                }

                Thread.Sleep(30);
            }
        }
        catch (IOException)
        {
            result = "远程连接中断。";
        }
        catch (Exception exception)
        {
            Log.Error("Attached loop failed.", exception);
            result = $"附着会话异常：{exception.Message}";
        }
        finally
        {
            try
            {
                lock (writer)
                {
                    writer.Write(QuitFrame);
                    writer.Flush();
                }
            }
            catch
            {
                // Worker may already be gone.
            }

            if (!process.WaitForExit(500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }

            renderer.LeaveAttachedMode();
        }

        return result;
    }

    private static void SendInput(BinaryWriter writer, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        lock (writer)
        {
            writer.Write(InputFrame);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Flush();
        }
    }

    /// <summary>
    /// Maintains a local confirmation line. It is intentionally separate from the
    /// mirrored console: some SSH clients do not echo while typing and the target
    /// application may not repaint until Enter.
    /// </summary>
    private static void UpdateInputPreview(
        byte[] bytes,
        StringBuilder preview,
        ref string lastSubmitted,
        ref DateTime lastSubmittedAt)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var inEscape = false;
        foreach (var ch in text)
        {
            if (inEscape)
            {
                if (ch is >= '@' and <= '~' && ch is not '[' and not 'O')
                {
                    inEscape = false;
                }

                continue;
            }

            if (ch == '\u001b')
            {
                inEscape = true;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                if (preview.Length > 0)
                {
                    lastSubmitted = preview.ToString();
                    lastSubmittedAt = DateTime.UtcNow;
                    preview.Clear();
                }

                continue;
            }

            if (ch is '\b' or '\u007f')
            {
                if (preview.Length > 0)
                {
                    preview.Length--;
                }

                continue;
            }

            if (ch == '\u0003')
            {
                preview.Clear();
                lastSubmitted = "";
                continue;
            }

            if (ch < ' ')
            {
                continue;
            }

            if (preview.Length == 0)
            {
                lastSubmitted = "";
            }
            else if (preview.Length > 512)
            {
                preview.Remove(0, preview.Length - 512);
            }

            preview.Append(ch);
        }
    }

    /// <summary>
    /// Gives input confirmation priority when the status line is narrow.
    /// </summary>
    private static string BuildAttachedStatus(
        string baseStatus,
        StringBuilder preview,
        string lastSubmitted,
        DateTime lastSubmittedAt,
        int terminalWidth)
    {
        var width = Math.Max(20, terminalWidth - 8);
        var extra = "";
        if (preview.Length > 0)
        {
            extra = "输入: " + preview;
        }
        else if (!string.IsNullOrEmpty(lastSubmitted) &&
                 (DateTime.UtcNow - lastSubmittedAt).TotalSeconds < 3)
        {
            extra = "已提交: " + lastSubmitted;
        }

        if (extra.Length == 0)
        {
            return Truncate(baseStatus, width);
        }

        if (extra.Length + 3 >= width)
        {
            return TruncateTail(extra, width);
        }

        var availableBase = Math.Max(0, width - extra.Length - 3);
        var prefix = Truncate(baseStatus, availableBase);
        return string.IsNullOrEmpty(prefix) ? TruncateTail(extra, width) : prefix + " | " + extra;
    }

    private static string TruncateTail(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        return width <= 1 ? text[^width..] : "…" + text[^(width - 1)..];
    }

    /// <summary>Reads one length-framed snapshot produced by <see cref="WorkerCommand"/>.</summary>
    private static ConsoleSnapshot ReadSnapshot(BinaryReader reader)
    {
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var cursorX = reader.ReadInt32();
        var cursorY = reader.ReadInt32();
        var rows = new string[height];
        var attributes = new byte[height][];
        for (var row = 0; row < height; row++)
        {
            rows[row] = reader.ReadString();
            var length = reader.ReadInt32();
            attributes[row] = reader.ReadBytes(length);
        }

        return ConsoleSnapshot.Create(width, height, cursorX, cursorY, rows, attributes);
    }

    private static string BuildMenu(
        IReadOnlyList<ConsoleWindowInfo> windows,
        int selectedIndex,
        int windowsTerminalCount,
        string? notice,
        string numberBuffer,
        int terminalWidth,
        bool hasPty)
    {
        var width = Math.Max(40, terminalWidth - 1);
        var builder = new StringBuilder();
        builder.Append("Console SSH - 选择本机控制台窗口\r\n");
        builder.Append(new string('=', Math.Min(width, 64))).Append("\r\n\r\n");
        if (!hasPty)
        {
            builder.Append("注意：当前 SSH 连接未申请 PTY。若输入时看不到字符，请使用 ssh -tt 重新连接。\r\n\r\n");
        }

        if (windows.Count == 0)
        {
            builder.Append("没有找到可附着的经典控制台窗口。\r\n");
            builder.Append("请先在本机打开 cmd.exe，再按 R 刷新。\r\n");
        }
        else
        {
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                var marker = index == selectedIndex ? ">" : " ";
                var text = $"{marker} [{index + 1}] PID {window.ProcessId,-7} {window.ProcessName,-16} {window.Title}";
                builder.Append(Truncate(text, width)).Append("\r\n");
            }
        }

        if (windowsTerminalCount > 0)
        {
            builder.Append("\r\n注意：检测到 Windows Terminal 窗口。其标签页使用伪控制台，不能直接附着；");
            builder.Append("请在“默认终端应用程序”中改用 Windows 控制台宿主，或单独打开经典 cmd 窗口。\r\n");
        }

        if (!string.IsNullOrWhiteSpace(notice))
        {
            builder.Append("\r\n").Append(Truncate(notice, width)).Append("\r\n");
        }

        if (numberBuffer.Length > 0)
        {
            builder.Append("\r\n待确认编号: ").Append(numberBuffer).Append("\r\n");
        }

        builder.Append("\r\n操作：↑/↓ 或 J/K 选择，Enter 连接，输入编号后 Enter 连接。");
        builder.Append(" R 刷新，Q 退出。\r\n");
        builder.Append("远程端使用 Ctrl+] 返回本列表。\r\n");
        return builder.ToString();
    }

    private static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + "…";
    }

    private static MenuKey? ReadRawMenuKey(RemoteTerminal terminal)
    {
        if (!terminal.TryReadChunk(out var chunk, 100))
        {
            return null;
        }

        var first = chunk[0];
        if (first == 0x1B)
        {
            var bytes = new List<byte>(chunk);
            while (bytes.Count < 3 && terminal.TryReadChunk(out var more, 30))
            {
                bytes.AddRange(more);
            }

            if (bytes.Count >= 3 && bytes[1] == (byte)'[' && bytes[2] == (byte)'A')
            {
                Requeue(terminal, bytes.ToArray(), 3);
                return new MenuKey(MenuKeyKind.Up);
            }

            if (bytes.Count >= 3 && bytes[1] == (byte)'[' && bytes[2] == (byte)'B')
            {
                Requeue(terminal, bytes.ToArray(), 3);
                return new MenuKey(MenuKeyKind.Down);
            }

            Requeue(terminal, bytes.ToArray(), 1);
            return new MenuKey(MenuKeyKind.None);
        }

        Requeue(terminal, chunk, 1);
        return first switch
        {
            (byte)'\r' or (byte)'\n' => new MenuKey(MenuKeyKind.Enter),
            (byte)'q' or (byte)'Q' => new MenuKey(MenuKeyKind.Quit),
            (byte)'r' or (byte)'R' => new MenuKey(MenuKeyKind.Refresh),
            (byte)'k' or (byte)'K' => new MenuKey(MenuKeyKind.Up),
            (byte)'j' or (byte)'J' => new MenuKey(MenuKeyKind.Down),
            >= (byte)'0' and <= (byte)'9' => new MenuKey(MenuKeyKind.Digit, Character: (char)first),
            _ => new MenuKey(MenuKeyKind.None)
        };
    }

    private static MenuKey? ReadCookedMenuLine(RemoteTerminal terminal)
    {
        if (!terminal.TryReadLine(out var line))
        {
            Thread.Sleep(50);
            return null;
        }

        return new MenuKey(MenuKeyKind.Line, Line: line);
    }

    private static void Requeue(RemoteTerminal terminal, byte[] chunk, int consumed)
    {
        if (chunk.Length > consumed)
        {
            terminal.Enqueue(chunk.AsSpan(consumed).ToArray());
        }
    }

    private enum MenuKeyKind
    {
        None,
        Up,
        Down,
        Enter,
        Refresh,
        Quit,
        Digit,
        Line
    }

    private readonly record struct MenuKey(
        MenuKeyKind Kind,
        char Character = '\0',
        string Line = "");
}
