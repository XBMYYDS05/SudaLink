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

    static PortalAuthenticator()
    {
        // Dr.COM 的结果页常见 GB2312/GBK，.NET Core 默认不带这些代码页
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
            // 探测点并发跑，3.5 秒足够；再长只会在断网时让界面一直卡在"正在认证"
            Timeout = TimeSpan.FromSeconds(3.5)
        };
        _portalClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CampusNet", "2.0"));
        _internetClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CampusNet", "2.0"));
    }

    public async Task<bool> CheckInternetAsync(CancellationToken cancellationToken)
    {
        // 多个互相独立的探测点。校园网/运营商对单个探测点做劫持的情况很常见，
        // 只用一个会出现"明明能上网却一直判定离线"的假阴性。
        // 并发跑、谁先成功就返回：整体耗时约等于最慢的那个，而不是四个相加。
        var pending = new List<Task<bool>>
        {
            MatchesAsync(_internetClient, "http://www.msftconnecttest.com/connecttest.txt",
                "Microsoft Connect Test", cancellationToken),
            NoContentAsync(_internetClient, "http://connect.rom.miui.com/generate_204", cancellationToken),
            NoContentAsync(_internetClient, "https://connect.rom.miui.com/generate_204", cancellationToken),
            StatusOkAsync(_internetClient, "https://www.baidu.com/", cancellationToken)
        };

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            if (await finished)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 认证刚通过时网关还需要一点时间放行，立刻探测容易拿到"还没通"的结果；
    /// 但账号密码确实错误时又不该干等，所以重试次数由调用方按"有多不确定"来定。
    /// </summary>
    private async Task<bool> WaitForInternetAsync(int attempts, CancellationToken cancellationToken)
    {
        for (var index = 0; index < attempts; index++)
        {
            if (await CheckInternetAsync(cancellationToken))
                return true;
            if (index < attempts - 1)
                await Task.Delay(700, cancellationToken);
        }
        return false;
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

        // 169.254.x.x 是系统自己编的地址：没连上任何 DHCP，认证一定不会成功。
        // 直接说清楚，比让用户看着"网关返回 HTTP xxx"猜要好。
        if (localAddress.StartsWith("169.254.", StringComparison.Ordinal))
            return AuthResult.Fail("本机没有拿到校园网 IP，请断开后重新连接 WiFi");

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
            var status = (int)response.StatusCode;
            var responseText = await ReadBodyAsync(response, cancellationToken);
            var retCode = ReadRetCode(response.Headers.Location);

            // 响应本身能说明问题时先看响应，省掉一次联网探测
            var parsed = ParseLoginResponse(responseText);
            if (parsed.Success)
                return parsed;

            // 3xx 不等于失败：Dr.COM 在认证通过（或本机已在线）时通常 302 到结果页/首页，
            // 而正文常常是空的。所以凡是"可能是成功"的状态码，一律用真正能不能上网来判定。
            //   · 3xx / 空正文：最没把握，多试几次（网关放行需要一点时间）
            //   · 2xx 但有内容却解析不出结论：只试一次，避免密码错误时干等
            var ambiguous = status is >= 300 and < 400 || string.IsNullOrWhiteSpace(responseText);
            if (status < 400 && await WaitForInternetAsync(ambiguous ? 3 : 1, cancellationToken))
                return AuthResult.Ok(status is >= 300 and < 400 ? "网关已跳转，网络已连通" : "网关已确认联网");

            return EvaluateLoginResponse(status, responseText, internetReachable: false, retCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Fail("连接网关超时");
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("无法连接认证网关");
        }
        catch (InvalidOperationException)
        {
            // 后台循环里不能因为一次奇怪的响应就把守护线程带走
            return AuthResult.Fail("网关响应无法解析");
        }
    }

    /// <summary>
    /// 把"网关响应 + 是否真的能上网"合成为最终结论。
    /// 单独抽出来是为了可测：判定逻辑不该只能靠真网关验证。
    /// </summary>
    public static AuthResult EvaluateLoginResponse(int statusCode, string body, bool internetReachable,
        int? retCode = null)
    {
        var parsed = ParseLoginResponse(body);
        if (parsed.Success)
            return parsed;

        if (internetReachable)
            return AuthResult.Ok(statusCode is >= 300 and < 400 ? "网关已跳转，网络已连通" : "网关已确认联网");

        if (statusCode >= 400)
            return AuthResult.Fail($"网关返回 HTTP {statusCode}");

        if (statusCode is >= 300 and < 400)
            return AuthResult.Fail(retCode switch
            {
                2 => "网关说本机已在线，但实际仍然上不了网",
                null => $"网关跳转（HTTP {statusCode}）后网络仍不可用，可能是运营商侧未放行",
                _ => $"网关跳转（HTTP {statusCode}，RetCode={retCode}）后网络仍不可用"
            });

        return parsed;
    }

    /// <summary>Dr.COM 会把结果码放在跳转地址的查询串里，RetCode=2 通常表示本机已在线。</summary>
    private static int? ReadRetCode(Uri? location)
    {
        if (location is null)
            return null;

        var match = Regex.Match(location.Query, @"[?&]RetCode=(?<code>\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["code"].Value, out var code) ? code : null;
    }

    /// <summary>
    /// 网关的 Content-Type 常带 gb2312 之类 .NET 默认不认识的 charset，
    /// 直接 ReadAsStringAsync 会抛 InvalidOperationException，所以这里要能兜住。
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { return Encoding.GetEncoding(charset).GetString(bytes); }
                catch (ArgumentException) { }
            }
            return Encoding.UTF8.GetString(bytes);
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

    /// <summary>generate_204 类探测点：正常时返回 204 且没有正文。</summary>
    private static async Task<bool> NoContentAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.OK)
                return false;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length == 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>有正文的 200 才算通：被劫持到门户页时通常也是 200，但内容对不上。</summary>
    private static async Task<bool> StatusOkAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return false;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > 512;
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
