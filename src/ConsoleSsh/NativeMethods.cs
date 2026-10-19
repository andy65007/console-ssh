using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Win32 surface used by the service. Blittable structs and delegate signatures are
/// kept here so the rest of the code can stay in ordinary managed C#.
/// </summary>
internal static class NativeMethods
{
    // Console handles, input modes, and token/job constants.
    internal const int StdInputHandle = -10;
    internal const int StdOutputHandle = -11;
    internal const int StdErrorHandle = -12;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    internal const uint EnableProcessedInput = 0x0001;
    internal const uint EnableLineInput = 0x0002;
    internal const uint EnableEchoInput = 0x0004;
    internal const uint EnableWindowInput = 0x0008;
    internal const uint EnableMouseInput = 0x0010;
    internal const uint EnableInsertMode = 0x0020;
    internal const uint EnableQuickEditMode = 0x0040;
    internal const uint EnableExtendedFlags = 0x0080;
    internal const uint EnableVirtualTerminalInput = 0x0200;
    internal const uint EnableVirtualTerminalProcessing = 0x0004;

    internal const ushort KeyEvent = 0x0001;
    internal const uint LeftCtrlPressed = 0x0008;
    internal const uint RightCtrlPressed = 0x0004;
    internal const uint ShiftPressed = 0x0010;
    internal const uint AltPressed = 0x0002;

    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateSuspended = 0x00000004;
    internal const uint StartfUseStdHandles = 0x00000100;

    internal const uint TokenAllAccess = 0x000F01FF;
    internal const int TokenPrimary = 1;
    internal const int SecurityImpersonation = 2;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceStartPending = 0x00000002;
    internal const uint ServiceRunning = 0x00000004;
    internal const uint ServiceStopPending = 0x00000003;
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceAcceptStop = 0x00000001;
    internal const uint ServiceAcceptShutdown = 0x00000004;
    internal const uint ServiceControlStop = 0x00000001;
    internal const uint ServiceControlShutdown = 0x00000005;

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidHandle = 6;
    internal const int ErrorNotEnoughMemory = 8;
    internal const int ErrorInvalidParameter = 87;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate void ServiceMainDelegate(int argc, IntPtr argv);
    internal delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    // Console buffer and input union layouts. INPUT_RECORD's KeyEvent begins at
    // offset 4 because the event type is a USHORT followed by alignment padding.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        public short X;
        public short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CharInfo
    {
        public char UnicodeChar;
        public ushort Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct KeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    internal struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceTableEntry
    {
        public string? ServiceName;
        public IntPtr ServiceProc;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleScreenBufferInfo(IntPtr consoleOutput, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadConsoleOutputW(
        IntPtr consoleOutput,
        [Out] CharInfo[] buffer,
        Coord bufferSize,
        Coord bufferCoord,
        ref SmallRect readRegion);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteConsoleInputW(
        IntPtr consoleInput,
        InputRecord[] buffer,
        uint length,
        out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SetConsoleCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        ref JobObjectExtendedLimitInformation info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr RegisterServiceCtrlHandlerExW(
        string serviceName,
        HandlerExDelegate handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] serviceTable);
}
