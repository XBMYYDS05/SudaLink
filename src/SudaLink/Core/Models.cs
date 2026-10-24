using System.Text.Json.Serialization;

namespace SudaLink;

/// <summary>校内账号支持的运营商出口。</summary>
public static class Carriers
{
    public static readonly (string Name, string Suffix)[] All =
    [
        ("校园网", ""),
        ("移动", "@zgyd"),
        ("联通", "@cucc"),
        ("电信", "@ctc")
    ];

    public static string NameOf(string username)
    {
        var suffix = SuffixOf(username);
        foreach (var (name, value) in All)
            if (string.Equals(value, suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        return "校园网";
    }

    public static string SuffixOf(string username)
    {
        var at = username.IndexOf('@');
        return at < 0 ? string.Empty : username[at..];
    }

    public static string SuffixByName(string name) =>
        All.FirstOrDefault(c => c.Name == name).Suffix ?? string.Empty;
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsCurrent { get; set; }

    /// <summary>不带运营商后缀的学号。</summary>
    [JsonIgnore]
    public string Number
    {
        get
        {
            var at = Username.IndexOf('@');
            return at < 0 ? Username : Username[..at];
        }
    }

    [JsonIgnore]
    public string CarrierName => Carriers.NameOf(Username);

    [JsonIgnore]
    public string OperatorName => CarrierName;

    [JsonIgnore]
    public string DisplayDetails => CarrierName + (Enabled ? string.Empty : " · 已停用");

    public void SetLogin(string number, string carrierName)
    {
        var suffix = Carriers.SuffixByName(carrierName);
        Username = number.Trim() + suffix;
    }

    public Account Clone() => new()
    {
        Id = Id,
        Username = Username,
        ProtectedPassword = ProtectedPassword,
        Enabled = Enabled,
        Password = Password
    };
}

public sealed class AppSettings
{
    public bool AutoReconnect { get; set; } = true;
    public bool AutoHotspot { get; set; }
    public bool AutoStart { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int IntervalSeconds { get; set; } = 10;
    public string Gateway { get; set; } = "10.9.1.3";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class AppConfig
{
    public List<Account> Accounts { get; set; } = [];
    public string CurrentAccountId { get; set; } = string.Empty;
    public AppSettings Settings { get; set; } = new();

    public Account? CurrentAccount => Accounts.FirstOrDefault(a => a.Id == CurrentAccountId)
        ?? Accounts.FirstOrDefault(a => a.Enabled);

    public IEnumerable<Account> OrderedEnabledAccounts()
    {
        var current = CurrentAccount;
        if (current is not null && current.Enabled)
            yield return current;

        foreach (var account in Accounts.Where(a => a.Enabled && a.Id != current?.Id))
            yield return account;
    }
}

public enum ConnectionStatus
{
    Checking,
    Online,
    Offline,
    NoCampusNetwork,
    Authenticating,
    Failed,
    Paused
}

public sealed record NetworkSnapshot(
    ConnectionStatus Status,
    string Message,
    DateTime Timestamp,
    string? AccountName = null,
    int? NextCheckSeconds = null);

public sealed record LogEntry(DateTime Timestamp, string Level, string Message)
{
    public string Display => $"[{Timestamp:HH:mm:ss}]  {Message}";
}

public sealed record AuthResult(bool Success, string Message)
{
    public static AuthResult Ok(string message = "认证成功") => new(true, message);
    public static AuthResult Fail(string message) => new(false, message);
}

public sealed record ConfigLoadResult(AppConfig Config, bool Migrated, string? Warning = null);
