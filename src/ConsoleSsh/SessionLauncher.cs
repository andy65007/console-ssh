using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleSsh;

/// <summary>
/// Service-side wrapper around the agent process and its kill-on-close job.
/// </summary>
internal sealed class SessionAgentProcess : IDisposable
{
    private IntPtr _processHandle;
    private IntPtr _jobHandle;

    public SessionAgentProcess(uint processId, IntPtr processHandle, IntPtr jobHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _jobHandle = jobHandle;
    }

    public uint ProcessId { get; }

    public bool HasExited =>
        _processHandle == IntPtr.Zero ||
        NativeMethods.WaitForSingleObject(_processHandle, 0) == 0;

    public void Dispose()
    {
        Stop();
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    public void Stop()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            NativeMethods.TerminateJobObject(_jobHandle, 0);
            NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        if (_processHandle != IntPtr.Zero)
        {
            if (NativeMethods.WaitForSingleObject(_processHandle, 3000) != 0)
            {
                NativeMethods.TerminateProcess(_processHandle, 0);
                NativeMethods.WaitForSingleObject(_processHandle, 1000);
            }
        }
    }
}

/// <summary>
/// Starts the user-session launcher with the interactive user's primary token.
/// This is the bridge from the Session 0 service to the user's desktop console.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>
    /// Creates the agent suspended so it can be assigned to the job before it starts
    /// accepting SSH connections. This avoids orphaned sshd processes on service stop.
    /// </summary>
    public static SessionAgentProcess Launch(
        uint sessionId,
        string executable,
        string globalConfigPath,
        AppConfig config)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr job = IntPtr.Zero;
        try
        {
            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.TokenAllAccess,
                    IntPtr.Zero,
                    NativeMethods.SecurityImpersonation,
                    NativeMethods.TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            if (!NativeMethods.CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
            }

            var commandLine = new StringBuilder();
            commandLine.Append(AppPaths.QuoteCommandLineArgument(executable));
            commandLine.Append(" agent");
            commandLine.Append(" --config ").Append(AppPaths.QuoteCommandLineArgument(globalConfigPath));
            commandLine.Append(" --port ").Append(config.Port);
            commandLine.Append(" --bind ").Append(AppPaths.QuoteCommandLineArgument(config.ListenAddress));
            if (!string.IsNullOrWhiteSpace(config.AuthorizedKeysFile))
            {
                commandLine.Append(" --authorized-keys ")
                    .Append(AppPaths.QuoteCommandLineArgument(config.AuthorizedKeysFile));
            }

            if (!string.IsNullOrWhiteSpace(config.OpenSshDirectory))
            {
                commandLine.Append(" --openssh ")
                    .Append(AppPaths.QuoteCommandLineArgument(config.OpenSshDirectory));
            }

            var startupInfo = new NativeMethods.StartupInfo
            {
                Size = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                Desktop = "winsta0\\default"
            };
            const uint creationFlags =
                NativeMethods.CreateUnicodeEnvironment |
                NativeMethods.CreateNoWindow |
                NativeMethods.CreateSuspended;
            if (!NativeMethods.CreateProcessAsUserW(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    environment,
                    AppContext.BaseDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            var threadHandle = processInformation.Thread;
            job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (job != IntPtr.Zero)
            {
                var limitInfo = new NativeMethods.JobObjectExtendedLimitInformation();
                limitInfo.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
                NativeMethods.SetInformationJobObject(
                    job,
                    NativeMethods.JobObjectExtendedLimitInformationClass,
                    ref limitInfo,
                    (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());
                if (!NativeMethods.AssignProcessToJobObject(job, processInformation.Process))
                {
                    Log.Warn($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
                }
            }

            NativeMethods.ResumeThread(threadHandle);
            NativeMethods.CloseHandle(threadHandle);
            var processHandle = processInformation.Process;
            return new SessionAgentProcess(processInformation.ProcessId, processHandle, job);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(primaryToken);
            }

            NativeMethods.CloseHandle(userToken);
        }
    }
}
