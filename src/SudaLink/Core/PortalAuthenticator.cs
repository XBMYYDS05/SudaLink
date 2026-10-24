using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SudaLink;

public sealed partial class PortalAuthenticator : IDisposable
{
    private readonly HttpClient _portalClient;
    private readonly HttpClient _internetClient;

    public PortalAuthenticator()
    {
        _portalClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        _internetClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _portalClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CampusNet", "2.0"));
        _internetClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CampusNet", "2.0"));
    }

    public async Task<bool> CheckInternetAsync(CancellationToken cancellationToken)
    {
        if (await MatchesAsync(_internetClient, "http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test", cancellationToken))
            return true;
        return await MatchesAsync(_internetClient, "http://detectportal.firefox.com/success.txt", "success", cancellationToken);
    }

    public async Task<bool> IsGatewayReachableAsync(string gateway, CancellationToken cancellationToken)
    {
        ValidateGateway(gateway);
        try
        {
            using var response = await _portalClient.GetAsync($"http://{gateway}/?isReback=1",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<AuthResult> LoginAsync(string gateway, Account account, CancellationToken cancellationToken)
    {
        ValidateGateway(gateway);
        if (string.IsNullOrWhiteSpace(account.Username))
            return AuthResult.Fail("账号为空");

        string localAddress;
        try
        {
            localAddress = GetLocalIPv4(gateway);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            return AuthResult.Fail("无法确定本机校园网 IPv4 地址");
        }

        var values = new Dictionary<string, string>
        {
            ["DDDDD"] = account.Username,
            ["upass"] = account.Password,
            ["R1"] = "0",
            ["R2"] = "0",
            ["R6"] = "0",
            ["para"] = "00",
            ["0MKKey"] = string.Empty
        };

        try
        {
            using var content = new FormUrlEncodedContent(values);
            using var response = await _portalClient.PostAsync(
                $"http://{gateway}:801/eportal/?c=ACSetting&a=Login", content, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ParseLoginResponse(responseText);
            if (result.Success)
                return result;

            if (!response.IsSuccessStatusCode)
                return AuthResult.Fail($"网关返回 HTTP {(int)response.StatusCode}");

            if (await CheckInternetAsync(cancellationToken))
                return AuthResult.Ok("网关已确认联网");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Fail("连接网关超时");
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("无法连接认证网关");
        }
    }

    public static AuthResult ParseLoginResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return AuthResult.Fail("网关未返回内容");

        var text = response.Trim();
        if (Regex.IsMatch(text, "\\\"result\\\"\\s*:\\s*\\\"?1(?:\\D|$)", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, @"Dr\.COMWebLoginID_3\s*=\s*1(?:\D|$)", RegexOptions.IgnoreCase)
            || text.Contains("登录成功", StringComparison.OrdinalIgnoreCase)
            || text.Contains("已经成功登录", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Ok();

        if (text.Contains("密码", StringComparison.OrdinalIgnoreCase)
            || text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("upass", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail("账号或密码错误");
        if (text.Contains("设备", StringComparison.OrdinalIgnoreCase)
            || text.Contains("数量超限", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail("在线设备数量超限");
        if (text.Contains("欠费", StringComparison.OrdinalIgnoreCase)
            || text.Contains("余额", StringComparison.OrdinalIgnoreCase)
            || text.Contains("arrear", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail("账号欠费或余额不足");
        if (text.Contains("停用", StringComparison.OrdinalIgnoreCase)
            || text.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail("账号已停用");
        if (text.Contains("MAC", StringComparison.OrdinalIgnoreCase)
            || text.Contains("绑定", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Fail("账号存在 MAC 绑定限制");

        var drcomCode = Regex.Match(text, @"Msg=(?<code>\d+);", RegexOptions.IgnoreCase).Groups["code"].Value;
        if (drcomCode.Length > 0)
            return AuthResult.Fail(drcomCode switch
            {
                "01" => "认证失败，请检查账号",
                "02" => "在线设备数量超限",
                "03" => "账号欠费",
                "04" => "账号已停用",
                "05" => "密码错误",
                "09" => "账号存在 MAC 绑定限制",
                _ => $"认证失败，网关错误码 {drcomCode}"
            });

        var message = Regex.Match(text, "\\\"msg\\\"\\s*:\\s*\\\"(?<message>[^\\\"]+)\\\"",
            RegexOptions.IgnoreCase).Groups["message"].Value;
        if (!string.IsNullOrWhiteSpace(message))
            return AuthResult.Fail(SanitizeMessage(message));

        var errorCode = Regex.Match(text, @"(?:ret_code|error|err)\s*[=:]\s*""?(?<code>\d+)",
            RegexOptions.IgnoreCase).Groups["code"].Value;
        return AuthResult.Fail(errorCode.Length > 0 ? $"认证失败，网关错误码 {errorCode}" : "认证失败，请检查账号、运营商和网关");
    }

    public static void ValidateGateway(string gateway)
    {
        if (!IPAddress.TryParse(gateway, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("网关必须是有效的 IPv4 地址。", nameof(gateway));

        var bytes = address.GetAddressBytes();
        var isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
        if (!isPrivate)
            throw new ArgumentException("为保护账号密码，仅允许使用校内私有网关地址。", nameof(gateway));
    }

    private static string GetLocalIPv4(string gateway)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Connect(gateway, 801);
        return ((IPEndPoint)(socket.Client.LocalEndPoint
            ?? throw new InvalidOperationException("缺少本机地址"))).Address.ToString();
    }

    private static string SanitizeMessage(string value)
    {
        var plain = Regex.Replace(value, "<[^>]+>", string.Empty).Trim();
        plain = Regex.Replace(plain, @"https?://\S+", string.Empty, RegexOptions.IgnoreCase);
        return plain.Length <= 120 ? plain : plain[..120];
    }

    private static async Task<bool> MatchesAsync(HttpClient client, string url, string expected, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return false;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _portalClient.Dispose();
        _internetClient.Dispose();
    }
}
