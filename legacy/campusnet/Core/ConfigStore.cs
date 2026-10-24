using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampusNetWpf;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseDirectory;

    public ConfigStore(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    public string ConfigPath => Path.Combine(_baseDirectory, "campusnet.json");
    private string AccountsPath => Path.Combine(_baseDirectory, "accounts.txt");
    private string CurrentPath => Path.Combine(_baseDirectory, "current.txt");
    private string SettingsPath => Path.Combine(_baseDirectory, "settings.txt");

    public ConfigLoadResult Load()
    {
        Directory.CreateDirectory(_baseDirectory);
        if (File.Exists(ConfigPath))
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                ?? throw new InvalidDataException("配置文件内容为空。");
            Normalize(config);
            foreach (var account in config.Accounts)
                account.Password = Unprotect(account.ProtectedPassword);
            return new ConfigLoadResult(config, false);
        }

        if (File.Exists(AccountsPath) || File.Exists(SettingsPath))
        {
            var legacy = LoadLegacy();
            return new ConfigLoadResult(legacy, true, "已导入旧版配置，密码将在保存后加密存储。");
        }

        return new ConfigLoadResult(new AppConfig(), false);
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        foreach (var account in config.Accounts)
            account.ProtectedPassword = Protect(account.Password);

        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, ConfigPath, true);
    }

    private AppConfig LoadLegacy()
    {
        var config = new AppConfig();
        if (File.Exists(SettingsPath))
        {
            foreach (var rawLine in File.ReadLines(SettingsPath))
            {
                var parts = rawLine.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                switch (parts[0])
                {
                    case "AutoReconnect":
                        config.Settings.AutoReconnect = ParseBool(parts[1], true);
                        break;
                    case "AutoHotspot":
                        config.Settings.AutoHotspot = ParseBool(parts[1], false);
                        break;
                    case "AutoStart":
                        config.Settings.AutoStart = ParseBool(parts[1], false);
                        break;
                    case "Interval":
                        if (int.TryParse(parts[1], out var interval))
                            config.Settings.IntervalSeconds = Math.Clamp(interval, 3, 300);
                        break;
                    case "Gateway":
                        if (!string.IsNullOrWhiteSpace(parts[1]))
                            config.Settings.Gateway = parts[1].Trim();
                        break;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(AccountsPath))
        {
            foreach (var rawLine in File.ReadLines(AccountsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                var separator = line.IndexOf('|');
                if (separator <= 0)
                    continue;
                var username = line[..separator].Trim();
                var password = line[(separator + 1)..];
                if (username.Length == 0 || !seen.Add(username))
                    continue;
                config.Accounts.Add(new Account { Username = username, Password = password, Enabled = true });
            }
        }

        var current = File.Exists(CurrentPath) ? File.ReadAllText(CurrentPath).Trim() : string.Empty;
        config.CurrentAccountId = config.Accounts.FirstOrDefault(a =>
            string.Equals(a.Username, current, StringComparison.OrdinalIgnoreCase))?.Id
            ?? config.Accounts.FirstOrDefault()?.Id
            ?? string.Empty;
        return config;
    }

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(value, out var result) ? result : fallback;

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("账号密码无法由当前 Windows 用户解密，请重新输入密码。", ex);
        }
    }

    private static void Normalize(AppConfig config)
    {
        config.Settings.IntervalSeconds = Math.Clamp(config.Settings.IntervalSeconds, 3, 300);
        config.Settings.Gateway = config.Settings.Gateway.Trim();
        config.Accounts = config.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Username))
            .GroupBy(a => a.Username.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var account in config.Accounts)
        {
            account.Username = account.Username.Trim();
            if (string.IsNullOrWhiteSpace(account.Id))
                account.Id = Guid.NewGuid().ToString("N");
        }
        if (config.Accounts.All(a => a.Id != config.CurrentAccountId))
            config.CurrentAccountId = config.Accounts.FirstOrDefault()?.Id ?? string.Empty;
    }
}
