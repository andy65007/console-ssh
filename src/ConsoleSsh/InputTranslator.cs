using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Converts SSH terminal bytes into Windows console INPUT_RECORDs. Printable Unicode
/// is sent through KEY_EVENT records, while the main terminal escape sequences are
/// translated to virtual-key events so cmd/PowerShell history and editing still work.
/// </summary>
internal sealed class InputTranslator
{
    private const ushort VkBack = 0x08;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;
    private const ushort VkPrior = 0x21;
    private const ushort VkNext = 0x22;
    private const ushort VkEnd = 0x23;
    private const ushort VkHome = 0x24;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkInsert = 0x2D;
    private const ushort VkDelete = 0x2E;
    private const ushort VkF1 = 0x70;

    private readonly IntPtr _consoleInput;
    private readonly List<byte> _pending = [];
    private readonly object _gate = new();
    private long _escapeGeneration;

    public InputTranslator(IntPtr consoleInput)
    {
        _consoleInput = consoleInput;
    }

    /// <summary>Adds a stream chunk and consumes every complete key or escape sequence.</summary>
    public void Process(byte[] bytes)
    {
        lock (_gate)
        {
            _pending.AddRange(bytes);
            ProcessPending(false);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            ProcessPending(true);
        }
    }

    private void ProcessPending(bool endOfStream)
    {
        while (_pending.Count > 0)
        {
            if (_pending[0] == 0x1B)
            {
                if (!TryProcessEscape(endOfStream))
                {
                    ScheduleStandaloneEscapeFlush();
                    return;
                }

                continue;
            }

            if (_pending[0] < 0x80)
            {
                var value = _pending[0];
                _pending.RemoveAt(0);
                SendAscii(value);
                continue;
            }

            var span = CollectionsMarshal.AsSpan(_pending);
            var status = Rune.DecodeFromUtf8(span, out var rune, out var consumed);
            if (status == OperationStatus.NeedMoreData && !endOfStream)
            {
                return;
            }

            if (status != OperationStatus.Done)
            {
                _pending.RemoveAt(0);
                continue;
            }

            _pending.RemoveRange(0, consumed);
            SendCharacter(rune.ToString()[0], 0);
        }
    }

    private bool TryProcessEscape(bool endOfStream)
    {
        if (_pending.Count == 1)
        {
            if (endOfStream)
            {
                _pending.RemoveAt(0);
                SendKey(VkEscape, '\u001b', 0);
                return true;
            }

            return false;
        }

        if (_pending[1] == (byte)'[')
        {
            var finalIndex = -1;
            for (var index = 2; index < _pending.Count; index++)
            {
                if (_pending[index] is >= 0x40 and <= 0x7E)
                {
                    finalIndex = index;
                    break;
                }
            }

            if (finalIndex < 0)
            {
                if (_pending.Count > 128)
                {
                    _pending.RemoveAt(0);
                    SendKey(VkEscape, '\u001b', 0);
                    return true;
                }

                return false;
            }

            var sequence = Encoding.ASCII.GetString(_pending.GetRange(2, finalIndex - 1).ToArray());
            var final = (char)_pending[finalIndex];
            _pending.RemoveRange(0, finalIndex + 1);
            HandleCsi(sequence, final);
            return true;
        }

        if (_pending[1] == (byte)'O')
        {
            if (_pending.Count < 3)
            {
                return false;
            }

            var final = (char)_pending[2];
            _pending.RemoveRange(0, 3);
            HandleSs3(final);
            return true;
        }

        _pending.RemoveAt(0);
        SendKey(VkEscape, '\u001b', 0);
        return true;
    }

    private void HandleCsi(string parameters, char final)
    {
        var modifier = ParseModifier(parameters);
        var state = ModifierState(modifier);
        switch (final)
        {
            case 'A':
                SendKey(VkUp, '\0', state);
                break;
            case 'B':
                SendKey(VkDown, '\0', state);
                break;
            case 'C':
                SendKey(VkRight, '\0', state);
                break;
            case 'D':
                SendKey(VkLeft, '\0', state);
                break;
            case 'H':
                SendKey(VkHome, '\0', state);
                break;
            case 'F':
                SendKey(VkEnd, '\0', state);
                break;
            case 'Z':
                SendKey(VkTab, '\t', NativeMethods.ShiftPressed);
                break;
            case '~':
                HandleTilde(parameters, state);
                break;
        }
    }

    private void HandleSs3(char final)
    {
        switch (final)
        {
            case 'A':
                SendKey(VkUp, '\0', 0);
                break;
            case 'B':
                SendKey(VkDown, '\0', 0);
                break;
            case 'C':
                SendKey(VkRight, '\0', 0);
                break;
            case 'D':
                SendKey(VkLeft, '\0', 0);
                break;
            case 'H':
                SendKey(VkHome, '\0', 0);
                break;
            case 'F':
                SendKey(VkEnd, '\0', 0);
                break;
            case 'P':
                SendKey(VkF1, '\0', 0);
                break;
            case 'Q':
                SendKey((ushort)(VkF1 + 1), '\0', 0);
                break;
            case 'R':
                SendKey((ushort)(VkF1 + 2), '\0', 0);
                break;
            case 'S':
                SendKey((ushort)(VkF1 + 3), '\0', 0);
                break;
        }
    }

    private void HandleTilde(string parameters, uint state)
    {
        var first = parameters.Split(';', 2)[0];
        if (!int.TryParse(first, out var code))
        {
            return;
        }

        switch (code)
        {
            case 1:
            case 7:
                SendKey(VkHome, '\0', state);
                break;
            case 2:
                SendKey(VkInsert, '\0', state);
                break;
            case 3:
                SendKey(VkDelete, '\0', state);
                break;
            case 4:
            case 8:
                SendKey(VkEnd, '\0', state);
                break;
            case 5:
                SendKey(VkPrior, '\0', state);
                break;
            case 6:
                SendKey(VkNext, '\0', state);
                break;
            case 11:
            case 12:
            case 13:
            case 14:
            case 15:
            case 17:
            case 18:
            case 19:
            case 20:
            case 21:
            case 23:
            case 24:
                SendKey(FunctionKeyForCode(code), '\0', state);
                break;
        }
    }

    private static ushort FunctionKeyForCode(int code) => code switch
    {
        11 => VkF1,
        12 => (ushort)(VkF1 + 1),
        13 => (ushort)(VkF1 + 2),
        14 => (ushort)(VkF1 + 3),
        15 => (ushort)(VkF1 + 4),
        17 => (ushort)(VkF1 + 5),
        18 => (ushort)(VkF1 + 6),
        19 => (ushort)(VkF1 + 7),
        20 => (ushort)(VkF1 + 8),
        21 => (ushort)(VkF1 + 9),
        23 => (ushort)(VkF1 + 10),
        24 => (ushort)(VkF1 + 11),
        _ => 0
    };

    private static int ParseModifier(string parameters)
    {
        var parts = parameters.Split(';');
        return parts.Length > 1 && int.TryParse(parts[^1], out var value) ? value : 0;
    }

    private static uint ModifierState(int modifier)
    {
        return modifier switch
        {
            2 => NativeMethods.ShiftPressed,
            3 => NativeMethods.AltPressed,
            5 => NativeMethods.LeftCtrlPressed,
            6 => NativeMethods.ShiftPressed | NativeMethods.LeftCtrlPressed,
            7 => NativeMethods.AltPressed | NativeMethods.LeftCtrlPressed,
            8 => NativeMethods.ShiftPressed | NativeMethods.AltPressed,
            _ => 0
        };
    }

    /// <summary>
    /// A lone ESC is ambiguous with the start of a CSI/SS3 sequence. Wait briefly for
    /// continuation bytes, then inject Escape if nothing else arrived.
    /// </summary>
    private void ScheduleStandaloneEscapeFlush()
    {
        if (_pending.Count != 1)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _escapeGeneration);
        _pending[0] = 0x1B;
        Task.Delay(25).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (generation == Interlocked.Read(ref _escapeGeneration) &&
                    _pending.Count == 1 &&
                    _pending[0] == 0x1B)
                {
                    _pending.Clear();
                    SendKey(VkEscape, '\u001b', 0);
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Maps ASCII control bytes to the corresponding console key records.</summary>
    private void SendAscii(byte value)
    {
        switch (value)
        {
            case 0x03:
                GenerateCtrlC();
                return;
            case 0x08:
            case 0x7F:
                SendKey(VkBack, '\b', 0);
                return;
            case 0x09:
                SendKey(VkTab, '\t', 0);
                return;
            case 0x0A:
            case 0x0D:
                SendKey(VkReturn, '\r', 0);
                return;
            }

        if (value is > 0 and < 27)
        {
            var letter = (ushort)('A' + value - 1);
            SendKey(letter, (char)value, NativeMethods.LeftCtrlPressed);
            return;
        }

        SendKey(0, (char)value, 0);
    }

    private void SendCharacter(char ch, uint state)
    {
        var scan = VkKeyScan(ch);
        if (scan < 0)
        {
            SendKey(0, ch, state);
            return;
        }

        var virtualKey = (ushort)(scan & 0xFF);
        var shiftState = (uint)((scan >> 8) & 0xFF);
        var controlState = state;
        if ((shiftState & 1) != 0)
        {
            controlState |= NativeMethods.ShiftPressed;
        }

        if ((shiftState & 2) != 0)
        {
            controlState |= NativeMethods.LeftCtrlPressed;
        }

        if ((shiftState & 4) != 0)
        {
            controlState |= NativeMethods.AltPressed;
        }

        SendKey(virtualKey, ch, controlState);
    }

    private void SendKey(ushort virtualKey, char character, uint state)
    {
        var records = new NativeMethods.InputRecord[2];
        records[0].EventType = NativeMethods.KeyEvent;
        records[0].KeyEvent.KeyDown = true;
        records[0].KeyEvent.RepeatCount = 1;
        records[0].KeyEvent.VirtualKeyCode = virtualKey;
        records[0].KeyEvent.UnicodeChar = character;
        records[0].KeyEvent.ControlKeyState = state;

        records[1].EventType = NativeMethods.KeyEvent;
        records[1].KeyEvent.KeyDown = false;
        records[1].KeyEvent.RepeatCount = 1;
        records[1].KeyEvent.VirtualKeyCode = virtualKey;
        records[1].KeyEvent.UnicodeChar = character;
        records[1].KeyEvent.ControlKeyState = state;

        NativeMethods.WriteConsoleInputW(_consoleInput, records, (uint)records.Length, out _);
    }

    private void GenerateCtrlC()
    {
        NativeMethods.GenerateConsoleCtrlEvent(0, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);
}
