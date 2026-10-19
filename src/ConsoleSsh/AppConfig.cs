using System.Text.Json;

namespace ConsoleSsh;

/// <summary>
/// Persisted service configuration. The file contains no credentials: SSH access is
/// controlled by the active user's authorized_keys file.
/// </summary>
internal sealed class AppConfig
{
    // Network and polling settings are shared by the service and the per-user agent.
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 2222;
    public int PollIntervalMs { get; set; } = 120;
    public int MaxSessions { get; set; } = 3;
    public int ClientAliveIntervalSeconds { get; set; } = 30;
    public int ClientAliveCountMax { get; set; } = 3;
    public string? UserName { get; set; }
    public string? AuthorizedKeysFile { get; set; }
    public string? OpenSshDirectory { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Loads JSON configuration, falling back to defaults when the file is absent.</summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        config.Validate();
        return config;
    }

    /// <summary>Writes a validated configuration atomically enough for a small config file.</summary>
    public void Save(string path)
    {
        Validate();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppConfig Clone()
    {
        return (AppConfig)MemberwiseClone();
    }

    /// <summary>Normalizes values that would otherwise make sshd fail or hammer the console.</summary>
    public void Validate()
    {
        ListenAddress = string.IsNullOrWhiteSpace(ListenAddress) ? "0.0.0.0" : ListenAddress.Trim();
        Port = Port is < 1 or > 65535 ? 2222 : Port;
        PollIntervalMs = Math.Clamp(PollIntervalMs, 50, 2000);
        MaxSessions = Math.Clamp(MaxSessions, 1, 20);
        ClientAliveIntervalSeconds = Math.Clamp(ClientAliveIntervalSeconds, 10, 3600);
        ClientAliveCountMax = Math.Clamp(ClientAliveCountMax, 1, 20);
    }
}
