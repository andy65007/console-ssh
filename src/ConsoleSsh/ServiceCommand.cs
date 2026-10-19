using System.Runtime.InteropServices;

namespace ConsoleSsh;

/// <summary>
/// Native Windows service host. The service runs in Session 0 and therefore cannot
/// attach to a user's console directly; it supervises one agent in the active
/// interactive session and lets that user-owned process run sshd.
/// </summary>
internal static class ServiceCommand
{
    public const string ServiceName = "ConsoleSsh";
    private static readonly ManualResetEventSlim StopEvent = new(false);
    private static NativeMethods.ServiceMainDelegate? _serviceMainDelegate;
    private static NativeMethods.HandlerExDelegate? _handlerDelegate;
    private static IntPtr _statusHandle;

    public static int Run()
    {
        Log.Initialize(Path.Combine(AppPaths.ServiceLogDirectory, "service.log"));
        try
        {
            Directory.CreateDirectory(AppPaths.ProgramDataDirectory);
            if (!File.Exists(AppPaths.GlobalConfigPath))
            {
                new AppConfig().Save(AppPaths.GlobalConfigPath);
                Log.Info($"Created default configuration at {AppPaths.GlobalConfigPath}.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("Unable to initialize ProgramData.", exception);
        }

        _serviceMainDelegate = ServiceMain;
        _handlerDelegate = ServiceControlHandler;
        var table = new NativeMethods.ServiceTableEntry[2];
        table[0].ServiceName = ServiceName;
        table[0].ServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate);
        table[1].ServiceName = null;
        table[1].ServiceProc = IntPtr.Zero;

        if (!NativeMethods.StartServiceCtrlDispatcherW(table))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error($"StartServiceCtrlDispatcher failed: {error}. Run the executable with the 'agent' command for console mode.");
            return error;
        }

        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = NativeMethods.RegisterServiceCtrlHandlerExW(ServiceName, _handlerDelegate!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log.Error($"RegisterServiceCtrlHandlerEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        SetStatus(NativeMethods.ServiceStartPending, 0, 3000);
        Log.Info("Windows service starting.");
        try
        {
            SetStatus(NativeMethods.ServiceRunning, NativeMethods.ServiceAcceptStop | NativeMethods.ServiceAcceptShutdown, 0);
            var loop = new Thread(ServiceLoop)
            {
                IsBackground = true,
                Name = "ConsoleSsh.ServiceLoop"
            };
            loop.Start();
            StopEvent.Wait();
            SetStatus(NativeMethods.ServiceStopPending, 0, 5000);
            loop.Join(10000);
        }
        catch (Exception exception)
        {
            Log.Error("Service main failed.", exception);
        }
        finally
        {
            SetStatus(NativeMethods.ServiceStopped, 0, 0);
            Log.Info("Windows service stopped.");
        }
    }

    private static uint ServiceControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is NativeMethods.ServiceControlStop or NativeMethods.ServiceControlShutdown)
        {
            Log.Info($"Service control {control} received.");
            StopEvent.Set();
        }

        return 0;
    }

    /// <summary>
    /// Keeps exactly one agent alive for the current physical console session.
    /// Fast user switching changes the session ID and triggers a clean restart.
    /// </summary>
    private static void ServiceLoop()
    {
        SessionAgentProcess? agent = null;
        var currentSession = uint.MaxValue;
        var lastLaunchAttempt = DateTime.MinValue;

        try
        {
            while (!StopEvent.IsSet)
            {
                var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
                if (sessionId == uint.MaxValue)
                {
                    if (agent is not null)
                    {
                        agent.Dispose();
                        agent = null;
                        currentSession = uint.MaxValue;
                        Log.Info("No active console session; agent stopped.");
                    }

                    StopEvent.Wait(5000);
                    continue;
                }

                if (agent is not null && (agent.HasExited || currentSession != sessionId))
                {
                    Log.Info($"Agent for session {currentSession} stopped or session changed.");
                    agent.Dispose();
                    agent = null;
                }

                if (agent is null)
                {
                    if ((DateTime.UtcNow - lastLaunchAttempt).TotalSeconds < 3)
                    {
                        StopEvent.Wait(1000);
                        continue;
                    }

                    lastLaunchAttempt = DateTime.UtcNow;
                    try
                    {
                        var config = AppConfig.Load(AppPaths.GlobalConfigPath);
                        config.Validate();
                        agent = SessionLauncher.Launch(
                            sessionId,
                            AppPaths.LaunchExecutablePath,
                            AppPaths.GlobalConfigPath,
                            config);
                        currentSession = sessionId;
                        Log.Info($"Started agent PID {agent.ProcessId} in session {sessionId}.");
                    }
                    catch (Exception exception)
                    {
                        Log.Warn($"Unable to start agent in session {sessionId}: {exception.Message}");
                    }
                }

                StopEvent.Wait(2000);
            }
        }
        finally
        {
            agent?.Dispose();
        }
    }

    private static void SetStatus(uint state, uint acceptedControls, uint waitHint)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        var status = new NativeMethods.ServiceStatus
        {
            ServiceType = NativeMethods.ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = 0,
            CheckPoint = state == NativeMethods.ServiceRunning ? 0u : 1u,
            WaitHint = waitHint
        };
        NativeMethods.SetServiceStatus(_statusHandle, ref status);
    }
}
