using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

/// <summary>
/// 設計 §7 / T13: 公式 Sign in with ChatGPT（直接 OAuth）+ Responses API の整文を、ループバックのモックへの実 HTTP で試験する
/// （実サービスへは接続していない）。token・コード・クライアント ID はすべてモックが発行するダミー値。
/// </summary>
public sealed class ChatGptTests : IDisposable
{
    private const string Raw = "えーと明日は３時にあのー会議です";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-chatgpt-" + Guid.NewGuid().ToString("N"));
    private readonly DpapiSecretProtector _protector = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly FakeChatGptServer _fake = new();
    private readonly TestHttpServer _server;
    private DateTimeOffset _now = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    public ChatGptTests()
    {
        Directory.CreateDirectory(_dir);
        _server = new TestHttpServer(_fake.Handle);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string TokenPath => Path.Combine(_dir, "chatgpt-token.json");

    private static NetworkContext Direct() => new(() => ProxyConfig.Environment, ProxyEnvironment.Empty);

    private ChatGptEndpoints Endpoints(int redirectPort = 0)
    {
        var b = $"http://127.0.0.1:{_server.Port}";
        return new ChatGptEndpoints(new Uri(b + "/api/accounts/authorize"), new Uri(b + "/api/accounts/oauth/token"), new Uri(b + "/v1/responses"), b + "/v1", redirectPort);
    }

    private ChatGptAuthService Service(int redirectPort = 0, NetworkContext? net = null) =>
        new(net ?? Direct(), new ChatGptTokenStore(TokenPath, _protector), Endpoints(redirectPort), () => _now);

    private static Dictionary<string, string> Query(Uri u) =>
        HttpUtility.ParseQueryString(u.Query).AllKeys.ToDictionary(k => k!, k => HttpUtility.ParseQueryString(u.Query)[k]!);

    private static async Task<HttpResponseMessage> Get(string url)
    {
        using var c = new HttpClient();
        return await c.GetAsync(url);
    }

    private static string CallbackUrl(Uri authorize, string code = "code-1", string clientId = "issued-client-123", string? state = null, string? extra = null)
    {
        var q = Query(authorize);
        return $"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(state ?? q["state"])}&client_id={clientId}{extra}";
    }

    /// <summary>ログインを一周させる（モックへの疑似コールバック）。</summary>
    private async Task<ChatGptAuthService> LoggedIn(Action? beforeCallback = null)
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        beforeCallback?.Invoke();
        using var resp = await Get(CallbackUrl(attempt.AuthorizeUrl));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        return svc;
    }

    // ---- 認可 URL ----

    [Fact]
    public void Authorize_url_carries_exactly_the_documented_parameters()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var u = attempt.AuthorizeUrl;
        Assert.Equal($"http://127.0.0.1:{_server.Port}/api/accounts/authorize", u.GetLeftPart(UriPartial.Path));
        var q = Query(u);
        Assert.Equal(new[] { "agent_name_hint", "client_id", "code_challenge", "code_challenge_method", "ext_agent_host_id", "nonce", "redirect_uri", "resource", "response_type", "scope", "state" },
            q.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("dynamic_agent_client", q["client_id"]);
        Assert.Equal("whisparrot", q["agent_name_hint"]);
        Assert.StartsWith("urn:uuid:", q["ext_agent_host_id"]);
        Assert.True(Guid.TryParse(q["ext_agent_host_id"]["urn:uuid:".Length..], out _));
        Assert.Equal("code", q["response_type"]);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/auth/callback$", q["redirect_uri"]);
        Assert.Equal($"http://127.0.0.1:{_server.Port}/v1", q["resource"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.NotEmpty(q["state"]);
        Assert.NotEmpty(q["nonce"]);
    }

    [Fact]
    public void Official_endpoints_are_the_documented_ones()
    {
        var o = ChatGptEndpoints.Official;
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", o.AuthorizeUrl.AbsoluteUri);
        Assert.Equal("https://auth.openai.com/api/accounts/oauth/token", o.TokenUrl.AbsoluteUri);
        Assert.Equal("https://api.openai.com/v1/responses", o.ResponsesUrl.AbsoluteUri);
        Assert.Equal("https://api.openai.com/v1", o.Resource);
        Assert.Equal(1455, o.RedirectPort);
    }

    // ---- コールバック・token 交換・保存 ----

    [Fact]
    public async Task Callback_exchanges_the_code_with_the_issued_client_id_and_PKCE_verifier_and_saves_encrypted()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var q = Query(attempt.AuthorizeUrl);
        using var resp = await Get(CallbackUrl(attempt.AuthorizeUrl, code: "the-code", clientId: "issued-client-123"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var status = await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ChatGptConnection.LoggedIn, status.State);

        var form = Assert.Single(_fake.TokenForms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("issued-client-123", form["client_id"]); // 動的クライアント ID ではなく、発行された ID
        Assert.Equal("the-code", form["code"]);
        Assert.Equal(q["redirect_uri"], form["redirect_uri"]);
        Assert.Equal($"http://127.0.0.1:{_server.Port}/v1", form["resource"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(q["code_challenge"], challenge);
        Assert.Equal("application/x-www-form-urlencoded", _fake.TokenContentTypes[0]);

        var saved = new ChatGptTokenStore(TokenPath, _protector).Load();
        Assert.Equal(TokenFileState.Ok, saved.State);
        Assert.Equal("issued-client-123", saved.Tokens!.ClientId);
        Assert.Equal("at-1", saved.Tokens.AccessToken);
        Assert.Equal("rt-1", saved.Tokens.RefreshToken);
        Assert.Equal(q["ext_agent_host_id"], "urn:uuid:" + saved.Tokens.InstallationId);
        Assert.Equal(_now.AddSeconds(3600), saved.Tokens.ExpiresAtUtc);
        var text = File.ReadAllText(TokenPath);
        Assert.DoesNotContain("at-1", text);
        Assert.DoesNotContain("rt-1", text);
        Assert.DoesNotContain("the-code", text);
    }

    [Fact]
    public async Task A_callback_with_the_wrong_state_is_refused_and_does_not_break_the_pending_login()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        using var bad = await Get(CallbackUrl(attempt.AuthorizeUrl, state: "forged"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(attempt.Completion.IsCompleted);
        Assert.Empty(_fake.TokenForms);

        using var good = await Get(CallbackUrl(attempt.AuthorizeUrl));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(ChatGptConnection.LoggedIn, (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
    }

    [Fact]
    public async Task Other_paths_and_methods_on_the_listener_are_not_served()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var origin = new Uri(Query(attempt.AuthorizeUrl)["redirect_uri"]).GetLeftPart(UriPartial.Authority);
        using var notFound = await Get(origin + "/other");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        using var c = new HttpClient();
        using var post = await c.PostAsync(origin + "/auth/callback", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.False(attempt.Completion.IsCompleted);
    }

    [Fact]
    public async Task A_denied_authorization_fails_the_login_without_calling_the_token_endpoint()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var q = Query(attempt.AuthorizeUrl);
        using var resp = await Get($"{q["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(q["state"])}");
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ChatGptLoginFailure.Denied, ex.Kind);
        Assert.Empty(_fake.TokenForms);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task A_callback_without_an_issued_client_id_fails()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var q = Query(attempt.AuthorizeUrl);
        using var resp = await Get($"{q["redirect_uri"]}?code=c&state={Uri.EscapeDataString(q["state"])}");
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ChatGptLoginFailure.InvalidCallback, ex.Kind);
        Assert.Empty(_fake.TokenForms);
    }

    [Fact]
    public void A_busy_redirect_port_is_reported_as_another_login_in_progress()
    {
        var svc = Service();
        using var first = svc.BeginLogin();
        var port = int.Parse(new Uri(Query(first.AuthorizeUrl)["redirect_uri"]).Port.ToString());
        var ex = Assert.Throws<ChatGptLoginException>(() => Service(redirectPort: port).BeginLogin());
        Assert.Equal(ChatGptLoginFailure.PortInUse, ex.Kind);
        Assert.Contains("別のログイン", ex.Message);
    }

    [Fact]
    public async Task The_port_is_released_after_the_login_finishes()
    {
        var svc = Service();
        int port;
        using (var attempt = svc.BeginLogin())
        {
            port = new Uri(Query(attempt.AuthorizeUrl)["redirect_uri"]).Port;
            using var resp = await Get(CallbackUrl(attempt.AuthorizeUrl));
            await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var again = Service(redirectPort: port).BeginLogin(); // 再び使える
        Assert.NotNull(again.AuthorizeUrl);
    }

    // ---- 手動貼り付け ----

    [Theory]
    [InlineData("url")]
    [InlineData("query")]
    [InlineData("url-with-spaces")]
    public async Task Pasting_the_redirect_url_completes_the_login(string form)
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        var url = CallbackUrl(attempt.AuthorizeUrl, code: "pasted-code", clientId: "issued-client-9");
        attempt.SubmitPasted(form switch { "query" => url[(url.IndexOf('?') + 1)..], "url-with-spaces" => "  " + url + "\r\n", _ => url });
        Assert.Equal(ChatGptConnection.LoggedIn, (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.Equal("pasted-code", Assert.Single(_fake.TokenForms)["code"]);
        Assert.Equal("issued-client-9", _fake.TokenForms[0]["client_id"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("http://127.0.0.1:1/auth/callback")]
    [InlineData("code=only")]
    public async Task Garbage_or_foreign_pastes_are_rejected_and_the_login_can_continue(string pasted)
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        Assert.Throws<ChatGptLoginException>(() => attempt.SubmitPasted(pasted));
        Assert.Throws<ChatGptLoginException>(() => attempt.SubmitPasted(CallbackUrl(attempt.AuthorizeUrl, state: "other-login")));
        Assert.False(attempt.Completion.IsCompleted);
        attempt.SubmitPasted(CallbackUrl(attempt.AuthorizeUrl));
        Assert.Equal(ChatGptConnection.LoggedIn, (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
    }

    [Fact]
    public async Task Cancel_ends_the_login_without_saving()
    {
        var svc = Service();
        using var attempt = svc.BeginLogin();
        attempt.Cancel();
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ChatGptLoginFailure.Cancelled, ex.Kind);
        Assert.False(File.Exists(TokenPath));
    }

    // ---- scope・拒否 ----

    [Fact]
    public async Task A_grant_without_the_direct_use_scope_is_a_failed_login_and_nothing_is_stored()
    {
        _fake.Scope = "openid profile email offline_access resource.invoke";
        var svc = Service();
        using var attempt = svc.BeginLogin();
        attempt.SubmitPasted(CallbackUrl(attempt.AuthorizeUrl));
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ChatGptLoginFailure.ScopeMissing, ex.Kind);
        Assert.False(File.Exists(TokenPath));
        Assert.Equal(ChatGptConnection.LoggedOut, svc.GetStatus().State);
    }

    [Theory]
    [InlineData(400, ChatGptLoginFailure.Rejected)]
    [InlineData(401, ChatGptLoginFailure.Rejected)]
    [InlineData(500, ChatGptLoginFailure.Network)]
    public async Task A_failing_token_endpoint_fails_the_login_with_no_leak(int status, ChatGptLoginFailure kind)
    {
        _fake.TokenStatus = status;
        var svc = Service();
        using var attempt = svc.BeginLogin();
        attempt.SubmitPasted(CallbackUrl(attempt.AuthorizeUrl, code: "secret-code-zzz"));
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("secret-code-zzz", ex.Message);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task A_token_response_without_a_refresh_token_or_scope_is_rejected()
    {
        _fake.OmitRefreshOnLogin = true;
        var svc = Service();
        using var attempt = svc.BeginLogin();
        attempt.SubmitPasted(CallbackUrl(attempt.AuthorizeUrl));
        var ex = await Assert.ThrowsAsync<ChatGptLoginException>(() => attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ChatGptLoginFailure.Rejected, ex.Kind);
    }

    // ---- 更新 ----

    [Fact]
    public async Task A_token_well_before_expiry_is_used_as_is_without_any_token_request()
    {
        var svc = await LoggedIn();
        _fake.TokenForms.Clear();
        _now = _now.AddSeconds(3600) - TimeSpan.FromMinutes(10);
        var r = await svc.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal("at-1", r.Token);
        Assert.Empty(_fake.TokenForms);
    }

    [Fact]
    public async Task A_token_within_three_minutes_of_expiry_is_refreshed_and_the_new_pair_is_stored()
    {
        var svc = await LoggedIn();
        _fake.TokenForms.Clear();
        _now = _now.AddSeconds(3600) - TimeSpan.FromMinutes(2);
        var r = await svc.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal("at-2", r.Token);

        var form = Assert.Single(_fake.TokenForms);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("issued-client-123", form["client_id"]);
        Assert.Equal("rt-1", form["refresh_token"]);
        Assert.Equal($"http://127.0.0.1:{_server.Port}/v1", form["resource"]);
        var saved = new ChatGptTokenStore(TokenPath, _protector).Load().Tokens!;
        Assert.Equal("at-2", saved.AccessToken);
        Assert.Equal("rt-2", saved.RefreshToken);
        Assert.Equal("issued-client-123", saved.ClientId);
        Assert.Equal(ChatGptConnection.LoggedIn, svc.GetStatus().State);
    }

    [Fact]
    public async Task A_refresh_response_without_a_new_refresh_token_keeps_the_old_one()
    {
        var svc = await LoggedIn();
        _fake.RotateRefresh = false;
        _now = _now.AddSeconds(3600);
        Assert.Equal("at-2", (await svc.GetAccessTokenAsync(CancellationToken.None)).Token);
        var saved = new ChatGptTokenStore(TokenPath, _protector).Load().Tokens!;
        Assert.Equal("rt-1", saved.RefreshToken);
        Assert.True(saved.HasRequiredScope);
    }

    [Fact]
    public async Task A_refreshed_grant_that_lost_the_scope_means_relogin()
    {
        var svc = await LoggedIn();
        _fake.Scope = "openid offline_access";
        _now = _now.AddSeconds(3600);
        var r = await svc.GetAccessTokenAsync(CancellationToken.None);
        Assert.Null(r.Token);
        Assert.Equal(PolishFailure.Auth, r.Failure);
        Assert.Equal(ChatGptConnection.NeedsRelogin, svc.GetStatus().State);
    }

    [Fact]
    public async Task A_rejected_refresh_marks_relogin_but_keeps_the_file_and_a_transient_failure_does_not()
    {
        var svc = await LoggedIn();
        _now = _now.AddSeconds(3600);

        _fake.TokenStatus = 503;
        var transient = await svc.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal(PolishFailure.Http, transient.Failure);
        Assert.Equal(ChatGptConnection.LoggedIn, svc.GetStatus().State);

        _fake.TokenStatus = 400; // invalid_grant など
        var rejected = await svc.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal(PolishFailure.Auth, rejected.Failure);
        Assert.Equal(ChatGptConnection.NeedsRelogin, svc.GetStatus().State);
        Assert.True(File.Exists(TokenPath)); // 勝手に消さない
        Assert.NotNull(svc.CheckReady());
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var svc = await LoggedIn();
        _fake.TokenForms.Clear();
        _now = _now.AddSeconds(3600);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => svc.GetAccessTokenAsync(CancellationToken.None)));
        Assert.All(results, r => Assert.Equal("at-2", r.Token));
        Assert.Single(_fake.TokenForms);
    }

    // ---- ログアウト・保存域 ----

    [Fact]
    public async Task Logout_deletes_the_token_file()
    {
        var svc = await LoggedIn();
        Assert.True(File.Exists(TokenPath));
        svc.Logout();
        Assert.False(File.Exists(TokenPath));
        Assert.Equal(ChatGptConnection.LoggedOut, svc.GetStatus().State);
        Assert.Equal(PolishFailure.Auth, (await svc.GetAccessTokenAsync(CancellationToken.None)).Failure);
        svc.Logout(); // 無くても失敗しない
    }

    [Fact]
    public async Task Installation_uuid_is_stable_across_relogin_while_the_file_exists()
    {
        var svc = await LoggedIn();
        var id = new ChatGptTokenStore(TokenPath, _protector).Load().Tokens!.InstallationId;
        using var attempt = svc.BeginLogin();
        Assert.Equal("urn:uuid:" + id, Query(attempt.AuthorizeUrl)["ext_agent_host_id"]);
    }

    [Fact]
    public void A_corrupt_token_file_is_unreadable_and_is_not_deleted()
    {
        File.WriteAllText(TokenPath, "{ not json");
        var svc = Service();
        Assert.Equal(ChatGptConnection.NeedsRelogin, svc.GetStatus().State);
        Assert.NotNull(svc.CheckReady());
        Assert.True(File.Exists(TokenPath));
        Assert.Equal("{ not json", File.ReadAllText(TokenPath));
    }

    [Fact]
    public void Ciphertext_from_another_user_is_unreadable_and_kept()
    {
        var other = new DpapiSecretProtector([9, 9, 9]);
        new ChatGptTokenStore(TokenPath, other).Save(new ChatGptTokens("c", Guid.NewGuid().ToString(), "A-SECRET", "R-SECRET", _now.AddHours(1), ChatGptEndpoints.Scope));
        var load = new ChatGptTokenStore(TokenPath, _protector).Load();
        Assert.Equal(TokenFileState.Unreadable, load.State);
        Assert.Null(load.Tokens);
        Assert.True(File.Exists(TokenPath));
    }

    [Fact]
    public void Token_values_never_appear_in_ToString_or_status()
    {
        var t = new ChatGptTokens("cid", "iid", "A-SECRET-VALUE", "R-SECRET-VALUE", _now, ChatGptEndpoints.Scope);
        Assert.DoesNotContain("SECRET", t.ToString());
        Assert.DoesNotContain("SECRET", t.ToString() + $"{t}");
    }

    // ---- 整文 adapter ----

    [Fact]
    public async Task Polish_posts_to_responses_with_the_bearer_token_and_the_documented_body()
    {
        var svc = await LoggedIn();
        var result = await new ChatGptPolisher(Direct(), svc, "test-model", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("明日は3時に会議です。", result.Text);

        var req = _server.Requests.Single(r => r.Target == "/v1/responses");
        Assert.Equal("POST", req.Method);
        Assert.Equal("Bearer at-1", req.Headers["Authorization"]);
        Assert.StartsWith("application/json", req.Headers["Content-Type"]);
        using var doc = JsonDocument.Parse(req.Body!);
        var root = doc.RootElement;
        Assert.Equal(new[] { "input", "instructions", "model", "store", "stream" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal(CompatiblePolisher.Instructions, root.GetProperty("instructions").GetString());
        Assert.Equal(Raw, root.GetProperty("input").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task Polish_refreshes_an_expiring_token_first_and_uses_the_new_one()
    {
        var svc = await LoggedIn();
        _now = _now.AddSeconds(3600) - TimeSpan.FromMinutes(1);
        var result = await new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer at-2", _server.Requests.Last(r => r.Target == "/v1/responses").Headers["Authorization"]);
    }

    [Fact]
    public async Task Polish_without_a_login_falls_back_with_Auth_and_sends_nothing()
    {
        var before = _server.Requests.Count;
        var r = await new ChatGptPolisher(Direct(), Service(), "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal(PolishFailure.Auth, r.Failure);
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public async Task Polish_with_a_failed_refresh_falls_back_and_never_calls_responses()
    {
        var svc = await LoggedIn();
        _now = _now.AddSeconds(3600);
        _fake.TokenStatus = 400;
        var r = await new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.Equal(PolishFailure.Auth, r.Failure);
        Assert.DoesNotContain(_server.Requests, q => q.Target == "/v1/responses");
    }

    [Theory]
    [InlineData(401, PolishFailure.Auth)]
    [InlineData(403, PolishFailure.Auth)]
    [InlineData(429, PolishFailure.Http)]
    [InlineData(500, PolishFailure.Http)]
    [InlineData(302, PolishFailure.Http)]
    public async Task Responses_http_errors_fall_back(int status, PolishFailure expected)
    {
        var svc = await LoggedIn();
        _fake.ResponsesOverride = _ => new TestResponse(status, Encoding.UTF8.GetBytes("{\"error\":\"at-1 echo\"}"), status == 302 ? new() { ["Location"] = "http://127.0.0.1:1/x" } : null);
        var r = await new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.Equal(expected, r.Failure);
        Assert.Equal($"HTTP {status}", r.Detail);
        Assert.DoesNotContain("at-1", r.Detail);
    }

    [Fact]
    public async Task A_hanging_responses_call_ends_at_the_deadline_with_Timeout()
    {
        var svc = await LoggedIn();
        var gate = new TaskCompletionSource();
        _fake.ResponsesOverride = _ => new TestResponse(200, Encoding.UTF8.GetBytes("{}"), HoldAfter: 0, Release: gate.Task);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(1)).PolishAsync(Raw, CancellationToken.None);
        gate.SetResult();
        Assert.Equal(PolishFailure.Timeout, r.Failure);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.8, 5);
    }

    [Fact]
    public async Task User_cancellation_propagates_instead_of_falling_back()
    {
        var svc = await LoggedIn();
        var gate = new TaskCompletionSource();
        _fake.ResponsesOverride = _ => new TestResponse(200, Encoding.UTF8.GetBytes("{}"), HoldAfter: 0, Release: gate.Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, cts.Token));
        gate.SetResult();
    }

    [Fact]
    public async Task An_invalid_proxy_configuration_falls_back_with_Network_and_never_goes_direct()
    {
        var svc = await LoggedIn();
        var bad = new NetworkContext(() => new ProxyConfig(ProxyMode.Manual, "not a url", "", null), ProxyEnvironment.Empty);
        var before = _server.Requests.Count;
        var r = await new ChatGptPolisher(bad, new ChatGptAuthService(bad, new ChatGptTokenStore(TokenPath, _protector), Endpoints(), () => _now), "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.Equal(PolishFailure.Network, r.Failure);
        Assert.Equal(before, _server.Requests.Count);
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public static TheoryData<string, PolishFailure?> ParseCases => new()
    {
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"整えた。"}]}]}""", null },
        { """{"status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"整えた。"}]}]}""", null },
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"整え"},{"type":"output_text","text":"た。"}]}]}""", null },
        { """{"status":"incomplete","output":[{"type":"message","content":[{"type":"output_text","text":"途中"}]}]}""", PolishFailure.Incomplete },
        { """{"status":"failed","output":[]}""", PolishFailure.Incomplete },
        { """{"output":[{"type":"message","content":[{"type":"output_text","text":"状態なし"}]}]}""", PolishFailure.Incomplete },
        { """{"status":"completed","error":{"message":"x"},"output":[]}""", PolishFailure.Malformed },
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""", PolishFailure.Malformed },
        { """{"status":"completed","output":[{"type":"function_call","name":"f","arguments":"{}"}]}""", PolishFailure.Malformed },
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"x"}]},{"type":"web_search_call"}]}""", PolishFailure.Malformed },
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":" \n "}]}]}""", PolishFailure.Empty },
        { """{"status":"completed","output":[]}""", PolishFailure.Empty },
        { """{"status":"completed","output":[{"type":"reasoning"}]}""", PolishFailure.Empty },
        { """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":5}]}]}""", PolishFailure.Malformed },
        { """{"status":"completed","output":"nope"}""", PolishFailure.Malformed },
        { """{"status":"completed"}""", PolishFailure.Malformed },
        { """[]""", PolishFailure.Malformed },
        { """<html>gateway</html>""", PolishFailure.Malformed },
        { "", PolishFailure.Malformed },
    };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Responses_parsing_accepts_only_a_completed_non_empty_message(string body, PolishFailure? failure)
    {
        var r = ChatGptPolisher.ParseResponse(B(body));
        if (failure is null) { Assert.True(r.IsSuccess); Assert.Equal("整えた。", r.Text); }
        else { Assert.False(r.IsSuccess); Assert.Equal(failure, r.Failure); }
    }

    [Fact]
    public async Task An_oversized_response_falls_back_with_TooLarge()
    {
        var svc = await LoggedIn();
        _fake.ResponsesOverride = _ => new TestResponse(200, new byte[CompatiblePolisher.MaxResponseBytes + 10]);
        var r = await new ChatGptPolisher(Direct(), svc, "m", TimeSpan.FromSeconds(15)).PolishAsync(Raw, CancellationToken.None);
        Assert.Equal(PolishFailure.TooLarge, r.Failure);
    }

    // ---- 設定からの構え ----

    private SettingsTextPolisher Polisher(AppSettings s, ChatGptAuthService svc) => new(() => s, _protector, Direct(), svc);

    private static AppSettings WithChatGpt(string model) =>
        AppSettings.Default with { Cleanup = AppSettings.Default.Cleanup with { Provider = CleanupProvider.ChatGpt, ChatGpt = new ChatGptCleanupSettings(model) } };

    [Fact]
    public async Task Setup_is_Ready_when_logged_in_and_a_model_is_chosen_and_uses_the_chosen_model()
    {
        var svc = await LoggedIn();
        var ready = Assert.IsType<PolishSetup.Ready>(Polisher(WithChatGpt(" chosen-model "), svc).Prepare());
        Assert.Equal(TimeSpan.FromSeconds(15), ready.Timeout);
        var r = await ready.PolishAsync(Raw, CancellationToken.None);
        Assert.True(r.IsSuccess);
        var req = _server.Requests.Last(q => q.Target == "/v1/responses");
        Assert.Equal("chosen-model", JsonDocument.Parse(req.Body!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Setup_without_a_model_is_unconfigured()
    {
        var svc = await LoggedIn();
        var u = Assert.IsType<PolishSetup.Unconfigured>(Polisher(WithChatGpt("  "), svc).Prepare());
        Assert.Contains("モデル", u.Reason);
    }

    [Fact]
    public void Setup_when_logged_out_is_unconfigured_prompts_settings_and_makes_no_network_call()
    {
        var before = _server.Requests.Count;
        var u = Assert.IsType<PolishSetup.Unconfigured>(Polisher(WithChatGpt("m"), Service()).Prepare());
        Assert.Contains("ログイン", u.Reason);
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public void Setup_with_a_stored_grant_lacking_the_scope_is_unconfigured()
    {
        new ChatGptTokenStore(TokenPath, _protector).Save(new ChatGptTokens("c", Guid.NewGuid().ToString(), "a", "r", _now.AddHours(1), "openid offline_access"));
        var u = Assert.IsType<PolishSetup.Unconfigured>(Polisher(WithChatGpt("m"), Service()).Prepare());
        Assert.Contains("scope", u.Reason);
    }

    [Fact]
    public async Task Off_and_compatible_do_not_touch_the_chatgpt_login()
    {
        var svc = await LoggedIn();
        Assert.IsType<PolishSetup.Off>(Polisher(AppSettings.Default, svc).Prepare());
        var before = _server.Requests.Count;
        svc.Logout();
        Assert.IsType<PolishSetup.Off>(Polisher(AppSettings.Default, svc).Prepare());
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public void Saving_the_chatgpt_provider_keeps_the_config_schema()
    {
        var path = Path.Combine(_dir, "config.json");
        var svc = new SettingsService(new JsonSettingsStore(path));
        svc.Update(_ => WithChatGpt("gpt-x"));
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("chatgpt", doc.RootElement.GetProperty("cleanup").GetProperty("provider").GetString());
        Assert.Equal("gpt-x", doc.RootElement.GetProperty("cleanup").GetProperty("chatgpt").GetProperty("model").GetString());
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase); // 資格は config.json に入らない
        Assert.Equal(WithChatGpt("gpt-x"), new JsonSettingsStore(path).Load().Settings);
    }
}

/// <summary>モックの認可サーバー（token エンドポイント）と Responses API。</summary>
internal sealed class FakeChatGptServer
{
    private readonly object _lock = new();
    private int _issued;

    public List<Dictionary<string, string>> TokenForms { get; } = [];
    public List<string> TokenContentTypes { get; } = [];
    public string Scope { get; set; } = ChatGptEndpoints.Scope;
    public int TokenStatus { get; set; } = 200;
    public bool RotateRefresh { get; set; } = true;
    public bool OmitRefreshOnLogin { get; set; }
    public Func<TestRequest, TestResponse>? ResponsesOverride { get; set; }

    public TestResponse Handle(TestRequest r)
    {
        lock (_lock)
        {
            var path = r.Target.Split('?')[0];
            if (path == "/api/accounts/oauth/token")
            {
                var form = HttpUtility.ParseQueryString(r.BodyText).AllKeys.ToDictionary(k => k!, k => HttpUtility.ParseQueryString(r.BodyText)[k]!);
                TokenForms.Add(form);
                TokenContentTypes.Add(r.Headers["Content-Type"]);
                if (TokenStatus != 200) return TestHttpServer.Json(TokenStatus, new { error = "invalid_grant" });
                var n = ++_issued;
                var isLogin = form["grant_type"] == "authorization_code";
                var body = new Dictionary<string, object> { ["access_token"] = $"at-{n}", ["token_type"] = "Bearer", ["expires_in"] = 3600, ["scope"] = Scope };
                if (isLogin ? !OmitRefreshOnLogin : RotateRefresh) body["refresh_token"] = $"rt-{n}";
                return TestHttpServer.Json(200, body);
            }
            if (path == "/v1/responses")
            {
                if (ResponsesOverride is { } o) return o(r);
                if (r.Headers.GetValueOrDefault("Authorization") != $"Bearer at-{_issued}") return TestHttpServer.Json(401, new { error = "bad token" });
                return TestHttpServer.Json(200, new
                {
                    id = "resp_1",
                    status = "completed",
                    output = new object[] { new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "明日は3時に会議です。" } } } },
                });
            }
            return new TestResponse(404, []);
        }
    }
}
