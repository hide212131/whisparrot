using System.Text;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

/// <summary>
/// プロキシ統合認証（既定の資格情報）。fixture は 407 チャレンジを返してメッセージの種別だけを記録する。
/// NTLM の暗号（Type 3 の中身）が正しいかは検証しない。確認できるのは「どの資格情報ソースで、どの順にメッセージが流れたか」まで。
/// </summary>
public sealed class IntegratedAuthRoutingTests
{
    private static readonly Uri Hf = new("https://huggingface.co/");

    private static NetworkContext Ctx(ProxyConfig cfg, string? envProxy = null) => new(() => cfg,
        ProxyEnvironment.From([KeyValuePair.Create("HTTPS_PROXY", envProxy)]));

    [Fact]
    public void Environment_mode_without_userinfo_uses_the_windows_logon()
    {
        Assert.Equal(ProxyAuthKind.WindowsLogon, Ctx(ProxyConfig.Environment, "http://proxy.local:8080").Describe(Hf).Auth);
    }

    [Fact]
    public void Environment_mode_userinfo_takes_precedence_over_the_windows_logon()
    {
        Assert.Equal(ProxyAuthKind.Credentials, Ctx(ProxyConfig.Environment, "http://alice:pw@proxy.local:8080").Describe(Hf).Auth);
    }

    [Fact]
    public void Manual_mode_windows_auth_ignores_a_stored_username()
    {
        var cfg = new ProxyConfig(ProxyMode.Manual, "http://proxy.local:8080", "alice", "pw", ProxyManualAuth.Windows);
        Assert.Equal(ProxyAuthKind.WindowsLogon, Ctx(cfg).Describe(Hf).Auth);
    }

    [Fact]
    public void Manual_mode_credentials_auth_keeps_the_previous_behaviour()
    {
        Assert.Equal(ProxyAuthKind.Credentials, Ctx(new ProxyConfig(ProxyMode.Manual, "http://p:1", "alice", "pw")).Describe(Hf).Auth);
        // ユーザー名が無ければ従来どおり資格情報なし（Windows のログオン情報には暗黙に切り替わらない）
        Assert.Equal(ProxyAuthKind.None, Ctx(new ProxyConfig(ProxyMode.Manual, "http://p:1", "", null)).Describe(Hf).Auth);
    }

    [Fact]
    public void Direct_routes_and_none_mode_have_no_proxy_auth()
    {
        Assert.Equal(ProxyAuthKind.None, Ctx(ProxyConfig.Environment).Describe(Hf).Auth);
        Assert.Equal(ProxyAuthKind.None, Ctx(new ProxyConfig(ProxyMode.None, "", "", null), "http://proxy.local:8080").Describe(Hf).Auth);
    }

    [Fact]
    public void Windows_logon_does_not_need_a_decryptable_password()
    {
        var p = new DpapiSecretProtector();
        var cfg = ProxyConfig.FromSettings(new ProxySettings(ProxyMode.Manual, "http://p:1", "u", "AAAA", ProxyManualAuth.Windows), p);
        Assert.Equal(ProxyManualAuth.Windows, cfg.ManualAuth);
        Assert.Null(cfg.ManualPassword);
    }

    [Fact]
    public void Windows_logon_credential_exposes_no_secret()
    {
        var c = ProxyTarget.WindowsLogon;
        Assert.Equal("", c.UserName);
        Assert.Equal("", c.Password);
    }
}

public sealed class IntegratedAuthSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_config_without_manual_auth_loads_with_the_default()
    {
        var r = SettingsJson.Parse("{\"schema_version\":1,\"proxy\":{\"mode\":\"manual\",\"manual_url\":\"http://p:1\",\"manual_username\":\"u\",\"manual_password_dpapi\":null}}", Now);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(ProxyManualAuth.Credentials, r.Settings.Proxy.ManualAuth);
        Assert.Equal(AppSettings.CurrentSchemaVersion, r.Settings.SchemaVersion);
    }

    [Fact]
    public void Manual_auth_windows_round_trips_and_the_default_is_not_written()
    {
        var s = AppSettings.Default with { Proxy = new ProxySettings(ProxyMode.Manual, "http://p:1", "", null, ProxyManualAuth.Windows) };
        var json = Encoding.UTF8.GetString(SettingsJson.Serialize(s));
        Assert.Contains("\"manual_auth\"", json);
        Assert.Contains("\"windows\"", json);
        Assert.Equal(s, SettingsJson.Parse(json, Now).Settings);

        // 既定のままなら、旧版が知らないキーを書き出さない
        Assert.DoesNotContain("manual_auth", Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default)));
    }

    [Theory]
    [InlineData("\"kerberos\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void An_unknown_manual_auth_is_rejected(string value)
    {
        var r = SettingsJson.Parse($"{{\"schema_version\":1,\"proxy\":{{\"manual_auth\":{value}}}}}", Now);
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.Contains(r.Issues, i => i.Path == "proxy.manual_auth");
    }

    [Fact]
    public void Schema_version_is_unchanged_and_a_future_schema_is_still_refused()
    {
        Assert.Equal(1, AppSettings.CurrentSchemaVersion);
        Assert.Equal(SettingsLoadStatus.UnsupportedSchema, SettingsJson.Parse("{\"schema_version\":2,\"proxy\":{\"manual_auth\":\"windows\"}}", Now).Status);
    }
}

/// <summary>
/// ループバックの 407 チャレンジ fixture。Windows の SSPI が実際に Type 1 / Type 3 を返すところまでを見る。
/// 既定の資格情報で Type 3 を作れるかはログオンセッションに依存する（パスワードを持たないセッションでは Type 1 の後で失敗し、
/// 非対話の SSH セッションでは匿名の Type 3 になる）。この試験は SSH セッションでの実行を前提にしている（docs/m5-report.md）。
/// </summary>
public sealed class IntegratedAuthWireTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private T Track<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

    private (TestTlsServer Server, TestConnectProxy Proxy, IntegratedAuthScript Script) Setup(ChallengeMode mode, bool acceptType3 = true)
    {
        var server = Track(new TestTlsServer(_ => new TestResponse(200, Encoding.ASCII.GetBytes("hello"))));
        var script = new IntegratedAuthScript(mode, acceptType3);
        var proxy = Track(new TestConnectProxy { IntegratedAuth = script });
        proxy.Map["models.test"] = server.Port;
        return (server, proxy, script);
    }

    private static NetworkContext Ctx(TestTlsServer trust, ProxyConfig cfg, string? envProxy = null) =>
        new(() => cfg, ProxyEnvironment.From([KeyValuePair.Create("HTTPS_PROXY", envProxy)]), trust.TrustThisServer);

    private static async Task<string> Get(NetworkContext net)
    {
        using var client = net.CreateClient(NetworkPurpose.ModelDownload, null, TimeSpan.FromSeconds(30));
        using var resp = await client.GetAsync("https://models.test/file");
        return $"{(int)resp.StatusCode}:{await resp.Content.ReadAsStringAsync()}";
    }

    /// <summary>成功を期待する経路。失敗時は fixture が見たメッセージの流れ（種別のみ）を添える。</summary>
    private static async Task<string> GetTraced(NetworkContext net, IntegratedAuthScript script)
    {
        try { return await Get(net); }
        catch (HttpRequestException ex) { throw new Xunit.Sdk.XunitException($"{ex.Message} events=[{string.Join(", ", script.Events)}]"); }
    }

    private static void AssertNtlmFlow(IReadOnlyList<string> events, string tag, string userLen = "user=0")
    {
        Assert.Equal(3, events.Count);
        Assert.Equal("none", events[0]);
        Assert.StartsWith(tag + "type1", events[1]);
        Assert.StartsWith(tag + "type3(" + userLen, events[2]);
    }

    [Fact]
    public async Task Environment_mode_answers_an_ntlm_challenge_with_type1_then_type3()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);

        Assert.Equal("200:hello", await GetTraced(net, script));
        AssertNtlmFlow(script.Events, "ntlm:", "user=");
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Manual_windows_auth_answers_an_ntlm_challenge_and_ignores_the_username_field()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm);
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "ignored-user", "ignored-pw", ProxyManualAuth.Windows));

        Assert.Equal("200:hello", await GetTraced(net, script));
        Assert.Equal(3, script.Events.Count);
        Assert.Equal("ntlm:type1", script.Events[1]);
        Assert.DoesNotContain("user=24", script.Events[2]); // "ignored-user"（UTF-16 で 24 バイト）で応答していない
    }

    [Fact]
    public async Task Userinfo_in_the_environment_url_is_used_instead_of_the_windows_logon()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url.Replace("http://", "http://alice:pw@"));

        Assert.Equal("200:hello", await GetTraced(net, script));
        Assert.Equal("ntlm:type1", script.Events[1]);
        Assert.StartsWith("ntlm:type3(user=10,", script.Events[2]); // "alice" = UTF-16 で 10 バイト
    }

    [Fact]
    public async Task Negotiate_challenge_is_answered_with_a_spnego_wrapped_ntlm_exchange()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Negotiate);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);

        Assert.Equal("200:hello", await GetTraced(net, script));
        AssertNtlmFlow(script.Events, "negotiate:", "user=");
    }

    [Fact]
    public async Task When_both_negotiate_and_ntlm_are_offered_the_client_completes_one_of_them()
    {
        var (server, proxy, script) = Setup(ChallengeMode.NtlmAndNegotiate);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);

        Assert.Equal("200:hello", await GetTraced(net, script));
        Assert.Equal("none", script.Events[0]);
        Assert.Contains(script.Events, e => e.Contains("type3("));
    }

    [Fact]
    public async Task Without_any_credential_source_the_client_does_not_answer_and_fails_without_going_direct()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm);
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null));

        await Assert.ThrowsAsync<HttpRequestException>(() => Get(net));
        Assert.Equal(["none"], script.Events);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_rejected_type3_fails_and_never_falls_back_to_direct()
    {
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm, acceptType3: false);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);

        await Assert.ThrowsAsync<HttpRequestException>(() => Get(net));
        Assert.Contains(script.Events, e => e.StartsWith("ntlm:type3("));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_basic_only_proxy_never_receives_the_windows_logon_as_a_basic_credential()
    {
        var (server, proxy, script) = Setup(ChallengeMode.BasicOnly);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);

        await Assert.ThrowsAsync<HttpRequestException>(() => Get(net));
        Assert.DoesNotContain("basic", script.Events);
        Assert.DoesNotContain(proxy.Connects, c => c.ProxyAuthorization?.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Windows_logon_is_not_sent_to_a_destination_outside_the_proxy_route()
    {
        // ループバックは常に直結。プロキシには何も届かない
        var (server, proxy, script) = Setup(ChallengeMode.Ntlm);
        var net = Ctx(server, ProxyConfig.Environment, proxy.Url);
        using var client = net.CreateClient(NetworkPurpose.ModelDownload, null, TimeSpan.FromSeconds(20));
        using var resp = await client.GetAsync($"https://127.0.0.1:{server.Port}/x");
        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Empty(proxy.Connects);
        Assert.Empty(script.Events);
    }
}
