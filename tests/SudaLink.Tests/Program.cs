using SudaLink;

var tests = new (string Name, Action Run)[]
{
    ("旧配置迁移与密码加密", TestLegacyMigration),
    ("网关成功响应解析", TestSuccessResponses),
    ("网关失败响应解析", TestFailureResponses),
    ("当前账号优先顺序", TestAccountOrdering),
    ("私有网关地址校验", TestGatewayValidation),
    ("运营商后缀与显示", TestCarriers)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void TestLegacyMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), "SudaLink.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllLines(Path.Combine(directory, "accounts.txt"), ["2330000001@zgyd|secret-one", "2330000002|secret-two"]);
        File.WriteAllText(Path.Combine(directory, "current.txt"), "2330000002");
        File.WriteAllLines(Path.Combine(directory, "settings.txt"),
            ["AutoReconnect=True", "AutoHotspot=True", "AutoStart=False", "Interval=10", "Gateway=10.9.1.3"]);
        var store = new ConfigStore(directory);
        var loaded = store.Load();
        Assert(loaded.Migrated, "未识别旧配置");
        Assert(loaded.Config.Accounts.Count == 2, "账号数量错误");
        Assert(loaded.Config.CurrentAccount?.Username == "2330000002", "当前账号迁移错误");
        Assert(loaded.Config.Settings.IntervalSeconds == 10 && loaded.Config.Settings.AutoHotspot, "设置迁移错误");
        store.Save(loaded.Config);
        Assert(Path.GetFileName(store.ConfigPath) == "sudalink.json", "配置文件未改名");
        var json = File.ReadAllText(store.ConfigPath);
        Assert(!json.Contains("secret-one") && !json.Contains("secret-two"), "新配置仍包含明文密码");
        var reloaded = new ConfigStore(directory).Load().Config;
        Assert(reloaded.Accounts[0].Password == "secret-one" && reloaded.Accounts[1].Password == "secret-two", "加密密码无法正确还原");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestSuccessResponses()
{
    Assert(PortalAuthenticator.ParseLoginResponse("dr1003({\"result\":1,\"msg\":\"success\"})").Success, "JSONP 成功响应未识别");
    Assert(PortalAuthenticator.ParseLoginResponse("<script>Dr.COMWebLoginID_3=1;</script>").Success, "Dr.COM 成功响应未识别");
    Assert(PortalAuthenticator.ParseLoginResponse("您已经成功登录。").Success, "中文成功响应未识别");
}

static void TestFailureResponses()
{
    Assert(!PortalAuthenticator.ParseLoginResponse("password error").Success, "密码错误未识别");
    Assert(PortalAuthenticator.ParseLoginResponse("password error").Message.Contains("密码"), "密码错误提示不准确");
    Assert(PortalAuthenticator.ParseLoginResponse("dr1003({\"result\":0,\"msg\":\"账号欠费\"})").Message.Contains("欠费"), "欠费提示不准确");
    Assert(PortalAuthenticator.ParseLoginResponse("Dr.COMWebLoginID_3=0;Msg=05;").Message.Contains("密码"), "Msg 错误码未映射");
    Assert(!PortalAuthenticator.ParseLoginResponse(string.Empty).Success, "空响应不应成功");
}

static void TestAccountOrdering()
{
    var first = new Account { Username = "first", Enabled = true };
    var second = new Account { Username = "second", Enabled = true };
    var disabled = new Account { Username = "disabled", Enabled = false };
    var config = new AppConfig { Accounts = [first, second, disabled], CurrentAccountId = second.Id };
    var ordered = config.OrderedEnabledAccounts().ToArray();
    Assert(ordered.Length == 2 && ordered[0] == second && ordered[1] == first, "账号尝试顺序错误");
}

static void TestGatewayValidation()
{
    PortalAuthenticator.ValidateGateway("10.9.1.3");
    PortalAuthenticator.ValidateGateway("192.168.1.1");
    Assert(Throws(() => PortalAuthenticator.ValidateGateway("8.8.8.8")), "公网网关不应通过校验");
    Assert(Throws(() => PortalAuthenticator.ValidateGateway("example.com")), "域名网关不应通过校验");
    Assert(Throws(() => PortalAuthenticator.ValidateGateway("not-an-ip")), "非法地址不应通过校验");
}

static void TestCarriers()
{
    var account = new Account();
    account.SetLogin("2330000001", "移动");
    Assert(account.Username == "2330000001@zgyd", "移动后缀错误");
    Assert(account.Number == "2330000001" && account.CarrierName == "移动", "学号或运营商显示错误");

    account.SetLogin("2330000002", "校园网");
    Assert(account.Username == "2330000002", "校园网不应带后缀");

    account.SetLogin("2330000003", "电信");
    Assert(account.CarrierName == "电信", "电信后缀解析错误");

    Assert(new Account { Username = "2330000004@cucc" }.CarrierName == "联通", "联通后缀解析错误");
    Assert(new Account { Username = "2330000005@unknown" }.CarrierName == "校园网", "未知后缀应回落到校园网");
}

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
