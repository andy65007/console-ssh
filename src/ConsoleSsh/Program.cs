using System.Globalization;

namespace ConsoleSsh;

/// <summary>
/// Process entry point and command dispatcher. One executable deliberately hosts
/// the Windows service, per-user agent, SSH endpoint, console worker, and installer.
/// </summary>
internal static class Program
{
    private const string Version = "1.0.1";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                if (Environment.UserInteractive)
                {
                    PrintHelp();
                    return 0;
                }

                return ServiceCommand.Run();
            }

            // A Windows service must call StartServiceCtrlDispatcher before doing work.
            // All other modes are ordinary console processes.
            return args[0].ToLowerInvariant() switch
            {
                "service" => ServiceCommand.Run(),
                "agent" => RunAgent(args.Skip(1).ToArray(), foreground: false),
                "run" => RunAgent(args.Skip(1).ToArray(), foreground: true),
                "attach" => RunAttach(args.Skip(1).ToArray()),
                "worker" => RunWorker(args.Skip(1).ToArray()),
                "install" => RunInstall(args.Skip(1).ToArray()),
                "uninstall" => RunUninstall(args.Skip(1).ToArray()),
                "status" => Installer.Status(),
                "version" or "--version" or "-v" => PrintVersion(),
                "help" or "--help" or "-h" => PrintHelpAndReturn(),
                _ => Unknown(args[0])
            };
        }
        catch (Exception exception)
        {
            Log.Error("Unhandled top-level failure.", exception);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunAgent(string[] args, bool foreground)
    {
        var options = new AgentOptions
        {
            ConfigPath = AppPaths.GlobalConfigPath,
            Foreground = foreground
        };
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    options.ConfigPath = Next(args, ref index);
                    break;
                case "--data-dir":
                    options.DataDirectory = Next(args, ref index);
                    break;
                case "--authorized-keys":
                    options.AuthorizedKeysFile = Next(args, ref index);
                    break;
                case "--openssh":
                    options.OpenSshDirectory = Next(args, ref index);
                    break;
                case "--port":
                    options.Port = int.Parse(Next(args, ref index), CultureInfo.InvariantCulture);
                    break;
                case "--bind":
                    options.ListenAddress = Next(args, ref index);
                    break;
                case "--foreground":
                    options.Foreground = true;
                    break;
                default:
                    throw new ArgumentException($"未知的 agent 参数：{args[index]}");
            }
        }

        return AgentCommand.Run(options);
    }

    private static int RunAttach(string[] args)
    {
        var configPath = Environment.GetEnvironmentVariable("CONSOLE_SSH_CONFIG") ?? AppPaths.GlobalConfigPath;
        string? dataDirectory = Environment.GetEnvironmentVariable("CONSOLE_SSH_DATA_DIR");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    configPath = Next(args, ref index);
                    break;
                case "--data-dir":
                    dataDirectory = Next(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"未知的 attach 参数：{args[index]}");
            }
        }

        AppConfig config;
        try
        {
            config = AppConfig.Load(configPath);
        }
        catch
        {
            config = new AppConfig();
        }

        return AttachCommand.Run(config, dataDirectory);
    }

    private static int RunWorker(string[] args)
    {
        uint processId = 0;
        var interval = 120;
        string? logPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pid":
                    processId = uint.Parse(Next(args, ref index), CultureInfo.InvariantCulture);
                    break;
                case "--interval":
                    interval = int.Parse(Next(args, ref index), CultureInfo.InvariantCulture);
                    break;
                case "--log":
                    logPath = Next(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"未知的 worker 参数：{args[index]}");
            }
        }

        if (processId == 0)
        {
            throw new ArgumentException("worker 需要 --pid。");
        }

        return WorkerCommand.Run(processId, interval, logPath);
    }

    private static int RunInstall(string[] args)
    {
        int? port = null;
        var firewall = true;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--port":
                    port = int.Parse(Next(args, ref index), CultureInfo.InvariantCulture);
                    break;
                case "--no-firewall":
                    firewall = false;
                    break;
                default:
                    throw new ArgumentException($"未知的 install 参数：{args[index]}");
            }
        }

        return Installer.Install(port, firewall);
    }

    private static int RunUninstall(string[] args)
    {
        return Installer.Uninstall(keepData: true);
    }

    private static string Next(string[] args, ref int index)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"参数 {args[index - 1]} 缺少值。");
        }

        return args[index];
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintHelp();
        return 2;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"console-ssh {Version}");
        return 0;
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
console-ssh {Version}

Windows 服务示例：
  sc.exe create ConsoleSsh binPath= "\"C:\Program Files\ConsoleSsh\console-ssh.exe\" service" start= auto
  sc.exe start ConsoleSsh

推荐使用 install.ps1，它会复制发布文件、注册服务并添加防火墙规则。

命令：
  service                       作为 Windows 服务运行（由 SCM 调用）
  agent --config <file>         在当前交互式用户会话中启动 SSH 代理
  run [--port 2222]             前台运行代理，适合调试
  attach                        远程 SSH 会话入口（由 sshd ForceCommand 调用）
  worker --pid <pid>            内部使用：附着到一个控制台进程
  install [--port 2222]         注册并启动 Windows 服务（需要管理员）
  uninstall                     停止并删除 Windows 服务（需要管理员）
  status                        查询服务状态
""");
    }
}
