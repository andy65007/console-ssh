namespace ConsoleSsh;

/// <summary>
/// Centralizes paths that differ between the SYSTEM service, the interactive user
/// agent, and a portable test run.
/// </summary>
internal static class AppPaths
{
    public static string ProgramDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ConsoleSsh");

    public static string GlobalConfigPath => Path.Combine(ProgramDataDirectory, "config.json");

    public static string ServiceLogDirectory => Path.Combine(ProgramDataDirectory, "logs");

    public static string UserDataDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            }

            return Path.Combine(local, "ConsoleSsh");
        }
    }

    public static string UserLogDirectory(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? UserDataDirectory, "logs");

    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "console-ssh.exe");

    public static string LaunchExecutablePath
    {
        get
        {
            var launcher = Path.Combine(AppContext.BaseDirectory, "console-ssh-launcher.exe");
            return File.Exists(launcher) ? launcher : CurrentExecutablePath;
        }
    }

    public static string OpenSshDirectory(string? configured = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "openssh"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "OpenSSH-Win64"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH"));

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, "sshd.exe")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "未找到便携版 OpenSSH。请把 sshd.exe 及其同目录文件放到应用的 openssh 子目录，或在配置中设置 openSshDirectory。");
    }

    public static string ResolveUserPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (expanded.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded["%USERPROFILE%".Length..].TrimStart('\\', '/'));
        }

        return Path.GetFullPath(expanded, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static string ToOpenSshPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>
    /// OpenSSH's PTY shell path loses quoting around ForceCommand on some clients.
    /// An NTFS 8.3 alias avoids whitespace without changing the installed location.
    /// </summary>
    public static string GetNoSpacePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Any(char.IsWhiteSpace))
        {
            return fullPath;
        }

        var buffer = new System.Text.StringBuilder(32768);
        var length = NativeMethods.GetShortPathNameW(fullPath, buffer, (uint)buffer.Capacity);
        if (length > 0 && length < buffer.Capacity)
        {
            var shortPath = buffer.ToString();
            if (!shortPath.Any(char.IsWhiteSpace))
            {
                return shortPath;
            }
        }

        return fullPath;
    }

    /// <summary>Quotes a single argument using the Windows CommandLineToArgvW rules.</summary>
    public static string QuoteCommandLineArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder();
        result.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(ch);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
