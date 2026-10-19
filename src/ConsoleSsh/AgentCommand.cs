using System.Diagnostics;
using System.Text;

namespace ConsoleSsh;

/// <summary>Command-line overrides used when the service starts the user agent.</summary>
internal sealed class AgentOptions
{
    public string? ConfigPath { get; set; }
    public string? DataDirectory { get; set; }
    public string? AuthorizedKeysFile { get; set; }
    public string? OpenSshDirectory { get; set; }
    public int? Port { get; set; }
    public string? ListenAddress { get; set; }
    public bool Foreground { get; set; }
}

/// <summary>
/// Prepares the user-owned SSH environment and supervises the bundled OpenSSH
/// process. sshd runs as the interactive user, so ForceCommand helpers can attach
/// to that user's console windows in the same session.
/// </summary>
internal static class AgentCommand
{
    /// <summary>Loads configuration, creates keys/config, then enters the sshd supervisor loop.</summary>
    public static int Run(AgentOptions options)
    {
        var dataDirectory = Path.GetFullPath(options.DataDirectory ?? AppPaths.UserDataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var logDirectory = AppPaths.UserLogDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        Log.Initialize(Path.Combine(logDirectory, "agent.log"), options.Foreground);

        var configPath = options.ConfigPath;
        var config = !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath)
            ? AppConfig.Load(configPath)
            : new AppConfig();
        if (options.Port.HasValue)
        {
            config.Port = options.Port.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.ListenAddress))
        {
            config.ListenAddress = options.ListenAddress;
        }

        if (!string.IsNullOrWhiteSpace(options.AuthorizedKeysFile))
        {
            config.AuthorizedKeysFile = options.AuthorizedKeysFile;
        }

        if (!string.IsNullOrWhiteSpace(options.OpenSshDirectory))
        {
            config.OpenSshDirectory = options.OpenSshDirectory;
        }

        config.Validate();

        try
        {
            Environment.SetEnvironmentVariable(
                "CONSOLE_SSH_CONFIG",
                configPath ?? AppPaths.GlobalConfigPath);
            Environment.SetEnvironmentVariable("CONSOLE_SSH_DATA_DIR", dataDirectory);
            var openSshDirectory = AppPaths.OpenSshDirectory(config.OpenSshDirectory);
            var sshdPath = Path.Combine(openSshDirectory, "sshd.exe");
            var keygenPath = Path.Combine(openSshDirectory, "ssh-keygen.exe");
            var hostKeyPath = Path.Combine(dataDirectory, "hostkeys", "ssh_host_ed25519_key");
            EnsureHostKey(keygenPath, hostKeyPath);

            var authorizedKeys = AppPaths.ResolveUserPath(config.AuthorizedKeysFile);
            EnsureAuthorizedKeysFile(authorizedKeys);

            var runtimeDirectory = Path.Combine(dataDirectory, "runtime");
            Directory.CreateDirectory(runtimeDirectory);
            var sshdConfigPath = Path.Combine(runtimeDirectory, "sshd_config");
            WriteSshdConfig(
                sshdConfigPath,
                config,
                hostKeyPath,
                authorizedKeys);

            Log.Info($"Using OpenSSH at {openSshDirectory}.");
            Log.Info($"Listening on {config.ListenAddress}:{config.Port} as {Environment.UserName}.");
            Log.Info($"Authorized keys: {authorizedKeys}");
            if (new FileInfo(authorizedKeys).Length == 0)
            {
                Log.Warn("authorized_keys 为空，远程登录会被拒绝。请把客户端公钥写入该文件。");
            }

            return SuperviseSshd(openSshDirectory, sshdPath, sshdConfigPath, Path.Combine(logDirectory, "sshd.log"));
        }
        catch (Exception exception)
        {
            Log.Error("Agent failed to initialize.", exception);
            return 1;
        }
    }

    /// <summary>Restarts sshd if it crashes, but exits cleanly on Ctrl+C or service shutdown.</summary>
    private static int SuperviseSshd(
        string openSshDirectory,
        string sshdPath,
        string sshdConfigPath,
        string sshdLogPath)
    {
        var stopping = 0;
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref stopping, 1);
            Log.Info("Stop requested from console.");
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = sshdPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = openSshDirectory,
                        RedirectStandardError = true
                    }
                };
                process.StartInfo.ArgumentList.Add("-D");
                process.StartInfo.ArgumentList.Add("-f");
                process.StartInfo.ArgumentList.Add(sshdConfigPath);
                process.StartInfo.ArgumentList.Add("-E");
                process.StartInfo.ArgumentList.Add(sshdLogPath);

                try
                {
                    if (!process.Start())
                    {
                        Log.Error("OpenSSH refused to start.");
                        return 2;
                    }
                }
                catch (Exception exception)
                {
                    Log.Error("Unable to start sshd.", exception);
                    Thread.Sleep(5000);
                    continue;
                }

                Log.Info($"sshd started, PID {process.Id}.");
                var stderrThread = new Thread(() =>
                {
                    try
                    {
                        var message = process.StandardError.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            Log.Warn("sshd stderr: " + message.Trim());
                        }
                    }
                    catch
                    {
                        // Process shutdown race.
                    }
                })
                {
                    IsBackground = true,
                    Name = "ConsoleSsh.SshdStderr"
                };
                stderrThread.Start();

                while (!process.HasExited &&
                       Volatile.Read(ref stopping) == 0)
                {
                    process.WaitForExit(500);
                }

                if (Volatile.Read(ref stopping) != 0)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Best effort.
                    }

                    break;
                }

                Log.Warn($"sshd exited with code {process.ExitCode}; restarting in 5 seconds.");
                Thread.Sleep(5000);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        return 0;
    }

    private static void EnsureHostKey(string keygenPath, string hostKeyPath)
    {
        if (File.Exists(hostKeyPath))
        {
            return;
        }

        if (!File.Exists(keygenPath))
        {
            throw new FileNotFoundException("ssh-keygen.exe 不存在。", keygenPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(hostKeyPath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = keygenPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("ed25519");
        startInfo.ArgumentList.Add("-N");
        startInfo.ArgumentList.Add("");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(hostKeyPath);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动 ssh-keygen.exe。");
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(hostKeyPath))
        {
            throw new InvalidOperationException($"生成 SSH 主机密钥失败，ssh-keygen 退出码 {process.ExitCode}。");
        }

        Log.Info("Generated a new Ed25519 SSH host key.");
    }

    private static void EnsureAuthorizedKeysFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, "", new UTF8Encoding(false));
            Log.Warn($"Created empty authorized_keys at {path}.");
        }
    }

    /// <summary>
    /// Generates a least-privilege sshd_config. ForceCommand deliberately uses the
    /// launcher rather than the managed apphost so the bundled .NET runtime is found.
    /// </summary>
    private static void WriteSshdConfig(
        string path,
        AppConfig config,
        string hostKeyPath,
        string authorizedKeysPath)
    {
        var runtimeDirectory = Path.GetDirectoryName(path)!;
        var executable = AppPaths.GetNoSpacePath(AppPaths.LaunchExecutablePath);
        if (executable.Any(char.IsWhiteSpace))
        {
            Log.Warn(
                "ForceCommand 路径仍包含空格，且系统没有可用的 8.3 短路径。请把程序安装到不含空格的目录，" +
                "否则交互式 PTY 会话可能无法启动。");
        }

        var attachArguments = $"{AppPaths.QuoteCommandLineArgument(executable)} attach";

        var builder = new StringBuilder();
        builder.AppendLine("# Generated by ConsoleSsh. Manual edits are overwritten.");
        builder.AppendLine($"Port {config.Port}");
        builder.AppendLine($"ListenAddress {config.ListenAddress}");
        builder.AppendLine($"HostKey \"{AppPaths.ToOpenSshPath(hostKeyPath)}\"");
        builder.AppendLine($"PidFile \"{AppPaths.ToOpenSshPath(Path.Combine(runtimeDirectory, "sshd.pid"))}\"");
        builder.AppendLine($"AuthorizedKeysFile \"{AppPaths.ToOpenSshPath(authorizedKeysPath)}\"");
        builder.AppendLine("PasswordAuthentication no");
        builder.AppendLine("KbdInteractiveAuthentication no");
        builder.AppendLine("PubkeyAuthentication yes");
        builder.AppendLine("AuthenticationMethods publickey");
        builder.AppendLine("HostbasedAuthentication no");
        builder.AppendLine("GSSAPIAuthentication no");
        builder.AppendLine("PermitEmptyPasswords no");
        builder.AppendLine("PermitRootLogin no");
        builder.AppendLine($"MaxAuthTries 3");
        builder.AppendLine($"MaxSessions {config.MaxSessions}");
        builder.AppendLine("LoginGraceTime 30");
        builder.AppendLine("PermitTTY yes");
        builder.AppendLine("PermitUserEnvironment no");
        builder.AppendLine("AllowTcpForwarding no");
        builder.AppendLine("AllowStreamLocalForwarding no");
        builder.AppendLine("AllowAgentForwarding no");
        builder.AppendLine("X11Forwarding no");
        builder.AppendLine("GatewayPorts no");
        builder.AppendLine("PermitTunnel no");
        builder.AppendLine("PermitOpen none");
        builder.AppendLine("StrictModes no");
        builder.AppendLine("UseDNS no");
        builder.AppendLine("PrintMotd no");
        builder.AppendLine("PrintLastLog no");
        builder.AppendLine($"ClientAliveInterval {config.ClientAliveIntervalSeconds}");
        builder.AppendLine($"ClientAliveCountMax {config.ClientAliveCountMax}");
        builder.AppendLine("LogLevel VERBOSE");
        var userName = string.IsNullOrWhiteSpace(config.UserName) ? Environment.UserName : config.UserName;
        builder.AppendLine($"AllowUsers \"{userName.Replace("\"", "")}\"");
        builder.AppendLine($"ForceCommand {attachArguments}");
        builder.AppendLine();

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        Log.Info($"Generated sshd config at {path}.");
    }
}
