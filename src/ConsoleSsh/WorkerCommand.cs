using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Per-console attachment worker. It leaves its inherited SSH PTY with FreeConsole,
/// attaches to the selected console, and exchanges framed snapshots/input over pipes.
///
/// Worker -> parent: 0x01 snapshot, 0x02 error, 0x03 exit.
/// Parent -> worker: 'I' length-prefixed input bytes, 'Q' quit.
/// </summary>
internal static class WorkerCommand
{
    private const byte SnapshotFrame = 1;
    private const byte ErrorFrame = 2;
    private const byte ExitFrame = 3;
    private const byte InputFrame = (byte)'I';
    private const byte QuitFrame = (byte)'Q';

    /// <summary>Runs until the target console disappears, the SSH channel closes, or quit is requested.</summary>
    public static int Run(uint targetProcessId, int pollIntervalMs, string? logPath)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            Log.Initialize(logPath);
        }

        Log.Info($"Worker starting for console PID {targetProcessId}.");
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        var writer = new BinaryWriter(stdout, new UTF8Encoding(false), leaveOpen: true);
        var writerGate = new object();
        var shuttingDown = 0;

        // The SSH endpoint normally owns a ConPTY/console. A process can attach to only
        // one console, so release it before AttachConsole(targetPid).
        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole(targetProcessId))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            SendError(writer, writerGate, $"无法附着到 PID {targetProcessId} 的控制台（Win32 {error}）。该窗口可能已关闭、属于更高权限，或不是经典控制台窗口。");
            Log.Error($"AttachConsole({targetProcessId}) failed: {error}");
            return 3;
        }

        // GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) targets every process sharing the
        // target console, including this worker. Ignore it locally so the worker survives.
        NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);
        var consoleOutput = NativeMethods.CreateFileW(
            "CONOUT$",
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        var consoleInput = NativeMethods.CreateFileW(
            "CONIN$",
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (consoleOutput == new IntPtr(-1) || consoleInput == new IntPtr(-1))
        {
            SendError(writer, writerGate, "附着成功，但无法打开目标控制台的输入/输出句柄。");
            return 4;
        }

        var reader = new ConsoleScreenReader(consoleOutput);
        var translator = new InputTranslator(consoleInput);
        // Wake the screen poller as soon as input is injected, making echo feel local.
        using var inputSignal = new ManualResetEventSlim(false);
        var inputThread = new Thread(() =>
        {
            try
            {
                using var binaryReader = new BinaryReader(stdin, new UTF8Encoding(false), leaveOpen: true);
                while (Volatile.Read(ref shuttingDown) == 0)
                {
                    var frame = binaryReader.ReadByte();
                    if (frame == InputFrame)
                    {
                        var length = binaryReader.ReadInt32();
                        if (length is < 0 or > 1024 * 1024)
                        {
                            break;
                        }

                        var bytes = binaryReader.ReadBytes(length);
                        if (bytes.Length != length)
                        {
                            break;
                        }

                        translator.Process(bytes);
                        inputSignal.Set();
                    }
                    else if (frame == QuitFrame)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // The SSH connection normally closes the worker input pipe.
            }
            finally
            {
                translator.Flush();
                Interlocked.Exchange(ref shuttingDown, 1);
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleSsh.WorkerInput"
        };
        inputThread.Start();

        string? lastKey = null;
        var interval = Math.Clamp(pollIntervalMs, 50, 2000);
        try
        {
            while (Volatile.Read(ref shuttingDown) == 0)
            {
                if (!reader.TryCapture(out var snapshot, out var error))
                {
                    Log.Warn(error);
                    SendError(writer, writerGate, error);
                    break;
                }

                if (snapshot is not null && snapshot.Key != lastKey)
                {
                    SendSnapshot(writer, writerGate, snapshot);
                    lastKey = snapshot.Key;
                }

                if (inputSignal.Wait(interval))
                {
                    inputSignal.Reset();
                    Thread.Sleep(10);
                }
            }
        }
        catch (IOException)
        {
            // Parent disconnected.
        }
        catch (Exception exception)
        {
            Log.Error("Worker loop failed.", exception);
            try
            {
                SendError(writer, writerGate, "工作进程异常，已退出。");
            }
            catch
            {
                // Ignore secondary pipe failures.
            }
        }
        finally
        {
            Interlocked.Exchange(ref shuttingDown, 1);
            try
            {
                lock (writerGate)
                {
                    writer.Write(ExitFrame);
                    writer.Flush();
                }
            }
            catch
            {
                // Parent is already gone.
            }

            reader.Dispose();
            NativeMethods.CloseHandle(consoleInput);
            NativeMethods.FreeConsole();
            Log.Info("Worker exited.");
        }

        return 0;
    }

    /// <summary>Writes a complete viewport snapshot; the parent performs ANSI diffing.</summary>
    private static void SendSnapshot(BinaryWriter writer, object gate, ConsoleSnapshot snapshot)
    {
        lock (gate)
        {
            writer.Write(SnapshotFrame);
            writer.Write(snapshot.Width);
            writer.Write(snapshot.Height);
            writer.Write(snapshot.CursorX);
            writer.Write(snapshot.CursorY);
            for (var row = 0; row < snapshot.Height; row++)
            {
                writer.Write(snapshot.Rows[row]);
                writer.Write(snapshot.Attributes[row].Length);
                writer.Write(snapshot.Attributes[row]);
            }

            writer.Flush();
        }
    }

    private static void SendError(BinaryWriter writer, object gate, string message)
    {
        try
        {
            lock (gate)
            {
                writer.Write(ErrorFrame);
                writer.Write(message);
                writer.Flush();
            }
        }
        catch
        {
            // The parent may have disconnected.
        }
    }
}
