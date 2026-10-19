using System.Diagnostics;
using System.Security.Principal;

namespace ConsoleSsh;

/// <summary>
/// Registers the launcher as an auto-start LocalSystem service and manages the
/// private/domain firewall rule. Installation is intentionally a separate,
/// elevated action.
/// </summary>
internal static class Installer
{
    public static int Install(int? portOverride, bool addFirewallRule)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("安装服务需要管理员权限。请在“以管理员身份运行”的 PowerShell 中执行。");
            return 5;
        }

        Directory.CreateDirectory(AppPaths.ProgramDataDirectory);
        var config = File.Exists(AppPaths.GlobalConfigPath)
            ? AppConfig.Load(AppPaths.GlobalConfigPath)
            : new AppConfig();
        if (portOverride.HasValue)
        {
            config.Port = portOverride.Value;
        }

        config.Validate();
        config.Save(AppPaths.GlobalConfigPath);

        var executable = AppPaths.LaunchExecutablePath;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "openssh", "sshd.exe")))
        {
            Console.Error.WriteLine("警告：应用目录下没有 openssh\\sshd.exe，服务启动后无法监听 SSH。");
        }

        RunIgnoreFailure("sc.exe", "stop", ServiceCommand.ServiceName);
        Thread.Sleep(800);
        RunIgnoreFailure("sc.exe", "delete", ServiceCommand.ServiceName);
        Thread.Sleep(300);
        var createExitCode = -1;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            createExitCode = RunIgnoreFailure("sc.exe",
                "create", ServiceCommand.ServiceName,
                "binPath=", $"{AppPaths.QuoteCommandLineArgument(executable)} service",
                "start=", "auto",
                "DisplayName=", "Console SSH Server");
            if (createExitCode == 0)
            {
                break;
            }

            Thread.Sleep(1000);
        }

        if (createExitCode != 0)
        {
            throw new InvalidOperationException($"注册 Windows 服务失败，sc.exe 退出码 {createExitCode}。");
        }
        RunIgnoreFailure("sc.exe", "description", ServiceCommand.ServiceName, "Remote SSH access to existing local console windows.");
        RunIgnoreFailure("sc.exe", "failure", ServiceCommand.ServiceName,
            "reset=", "86400",
            "actions=", "restart/5000/restart/5000/restart/5000");

        if (addFirewallRule)
        {
            RunIgnoreFailure("netsh.exe",
                "advfirewall", "firewall", "delete", "rule",
                $"name=Console SSH (TCP {config.Port})");
            RunIgnoreFailure("netsh.exe",
                "advfirewall", "firewall", "add", "rule",
                $"name=Console SSH (TCP {config.Port})",
                "dir=in", "action=allow", "protocol=TCP",
                $"localport={config.Port}",
                "profile=private,domain");
        }

        RunRequired("sc.exe", "start", ServiceCommand.ServiceName);
        Console.WriteLine($"服务已安装并启动。端口：{config.Port}，配置：{AppPaths.GlobalConfigPath}");
        return 0;
    }

    public static int Uninstall(bool keepData)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("卸载服务需要管理员权限。");
            return 5;
        }

        RunIgnoreFailure("sc.exe", "stop", ServiceCommand.ServiceName);
        Thread.Sleep(1000);
        RunIgnoreFailure("sc.exe", "delete", ServiceCommand.ServiceName);

        var config = File.Exists(AppPaths.GlobalConfigPath)
            ? AppConfig.Load(AppPaths.GlobalConfigPath)
            : new AppConfig();
        RunIgnoreFailure("netsh.exe",
            "advfirewall", "firewall", "delete", "rule",
            $"name=Console SSH (TCP {config.Port})");

        if (!keepData)
        {
            Console.WriteLine($"服务已卸载。数据目录仍保留在 {AppPaths.ProgramDataDirectory}。");
        }

        return 0;
    }

    public static int Status()
    {
        return RunIgnoreFailure("sc.exe", "query", ServiceCommand.ServiceName);
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RunRequired(string fileName, params string[] arguments)
    {
        var exitCode = RunIgnoreFailure(fileName, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} 失败，退出码 {exitCode}。");
        }

        return exitCode;
    }

    private static int RunIgnoreFailure(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return -1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
