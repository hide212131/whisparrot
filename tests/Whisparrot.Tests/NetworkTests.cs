using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

public sealed class NoProxyTests
{
    private static bool Hit(string noProxy, string url) => new NoProxyList(noProxy).Matches(new Uri(url));

    [Theory]
    [InlineData("*", "https://anything.example/", true)]
    [InlineData("example.com", "https://example.com/", true)]
    [InlineData("example.com", "https://EXAMPLE.com/", true)]
    [InlineData("example.com", "https://notexample.com/", false)]
    [InlineData("example.com", "https://api.example.com/", false)]      // 先頭ドットなしは完全一致
    [InlineData(".example.com", "https://api.example.com/", true)]
    [InlineData(".example.com", "https://a.b.example.com/", true)]
    [InlineData(".example.com", "https://example.com/", true)]
    [InlineData(".example.com", "https://notexample.com/", false)]
    [InlineData(".example.com", "https://example.com.evil.net/", false)]
    [InlineData("example.com:8443", "https://example.com:8443/", true)]
    [InlineData("example.com:8443", "https://example.com/", false)]
    [InlineData("10.1.2.3", "https://10.1.2.3/", true)]
    [InlineData("10.1.2.3", "https://10.1.2.30/", false)]
    [InlineData("10.1.2.3:8443", "https://10.1.2.3:8443/", true)]
    [InlineData("[::1]", "https://[::1]/", true)]
    [InlineData("[2001:db8::1]:8443", "https://[2001:db8::1]:8443/", true)]
    [InlineData("[2001:db8::1]:8443", "https://[2001:db8::1]/", false)]
    [InlineData(" a.example , b.example ", "https://b.example/", true)]
    [InlineData("a.example,,", "https://a.example/", true)]
    [InlineData("", "https://example.com/", false)]
    public void Matching_rules(string noProxy, string url, bool expected) => Assert.Equal(expected, Hit(noProxy, url));

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("*.example.com")]
    [InlineData("exa*ple.com")]
    [InlineData("^example\\.com$")]
    [InlineData("example.com:abc")]
    [InlineData("example.com:99999")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[example.com]")]
    [InlineData(".")]
    [InlineData(".10.0.0.1")]
    public void Unsupported_or_malformed_entries_are_configuration_errors(string noProxy)
    {
        Assert.Throws<NetworkConfigurationException>(() => new NoProxyList(noProxy));
    }
}

public sealed class ProxyRuleTests
{
    private static ProxyEnvironment Env(params (string, string?)[] kv) => ProxyEnvironment.From(kv.Select(p => KeyValuePair.Create(p.Item1, p.Item2)));

    [Fact]
    public void Environment_names_are_matched_case_insensitively_and_empty_means_unset()
    {
        Assert.Equal("http://p:1", Env(("https_proxy", "http://p:1")).HttpsProxy);
        Assert.Equal("http://p:1", Env(("Https_Proxy", " http://p:1 ")).HttpsProxy);
        Assert.Null(Env(("HTTPS_PROXY", "")).HttpsProxy);
        Assert.Null(Env(("HTTPS_PROXY", "  ")).HttpsProxy);
        Assert.Equal("a.example", Env(("no_proxy", "a.example")).NoProxy);
    }

    [Fact]
    public void Http_proxy_all_proxy_are_not_used_as_a_hidden_path()
    {
        var e = Env(("HTTP_PROXY", "http://x:1"), ("ALL_PROXY", "http://y:2"));
        Assert.Null(e.HttpsProxy);
    }

    [Fact]
    public void Conflicting_values_prefer_the_uppercase_key_and_are_reported()
    {
        var e = Env(("https_proxy", "http://lower:1"), ("HTTPS_PROXY", "http://upper:2"));
        Assert.Equal("http://upper:2", e.HttpsProxy);
        Assert.Single(e.Conflicts);
        Assert.Empty(Env(("https_proxy", "http://same:1"), ("HTTPS_PROXY", "http://same:1")).Conflicts);
    }

    [Theory]
    [InlineData("http://proxy.local:3128", "proxy.local:3128", null)]
    [InlineData("proxy.local:3128", "proxy.local:3128", null)]
    [InlineData("http://proxy.local", "proxy.local:80", null)]
    [InlineData("http://proxy.local:3128/", "proxy.local:3128", null)]
    [InlineData("http://user:p%40ss@proxy.local:3128", "proxy.local:3128", "user:p@ss")]
    [InlineData("http://user@proxy.local:3128", "proxy.local:3128", "user:")]
    [InlineData("https://proxy.local:3128", "proxy.local:3128", null)]
    [InlineData("https://proxy.local", "proxy.local:443", null)]
    [InlineData("https://user:p%40ss@proxy.local:8443/", "proxy.local:8443", "user:p@ss")]
    public void Proxy_url_is_normalized_and_userinfo_is_split_into_a_credential(string raw, string hostPort, string? cred)
    {
        var t = ProxyTarget.Parse(raw, "test");
        Assert.Equal(hostPort, t.HostPort);
        Assert.DoesNotContain("@", t.Uri.ToString());
        Assert.Equal(raw.StartsWith("https://") ? "https" : "http", t.Uri.Scheme);
        Assert.Equal(cred, t.Credential is null ? null : $"{t.Credential.UserName}:{t.Credential.Password}");
    }

    [Theory]
    [InlineData("socks5://proxy.local:1080")]
    [InlineData("http://proxy.local:3128/path")]
    [InlineData("https://proxy.local:3128/path")]
    [InlineData("https://proxy.local:3128?x=1")]
    [InlineData("ftp://proxy.local:3128")]
    [InlineData("http://proxy.local:3128?x=1")]
    [InlineData("http://proxy .local:3128")]
    [InlineData("http://:3128")]
    [InlineData("")]
    public void Invalid_proxy_urls_are_errors_never_a_silent_direct(string raw)
    {
        Assert.Throws<NetworkConfigurationException>(() => ProxyTarget.Parse(raw, "test"));
    }

    [Fact]
    public void Routing_proxy_sends_non_loopback_to_the_proxy_unless_bypassed()
    {
        var p = new RoutingProxy(ProxyTarget.Parse("http://proxy.local:3128", "t"), new NoProxyList("direct.example,.corp.example"));
        Assert.Equal("proxy.local", p.GetProxy(new Uri("https://huggingface.co/x"))!.Host);
        Assert.Null(p.GetProxy(new Uri("https://direct.example/")));
        Assert.Null(p.GetProxy(new Uri("https://a.corp.example/")));
        Assert.NotNull(p.GetProxy(new Uri("https://notdirect.example/")));
        foreach (var lb in new[] { "https://127.0.0.1/", "https://localhost/", "https://[::1]/", "https://127.5.5.5/" })
            Assert.True(p.IsBypassed(new Uri(lb)), lb);
    }

    [Fact]
    public void Routing_proxy_without_a_target_is_always_direct()
    {
        var p = new RoutingProxy(null, NoProxyList.Empty);
        Assert.Null(p.GetProxy(new Uri("https://example.com/")));
    }
}

/// <summary>ループバックの TLS サーバーと記録用 CONNECT プロキシによる結合試験（実プロキシ環境ではない）。</summary>
public sealed class NetworkIntegrationTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private T Track<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

    private TestTlsServer Server(Func<TestRequest, TestResponse>? handler = null) =>
        Track(new TestTlsServer(handler ?? (_ => new TestResponse(200, Encoding.ASCII.GetBytes("hello")))));

    private TestConnectProxy Proxy(TestTlsServer? server = null, string? auth = null, params string[] hosts)
    {
        var p = Track(new TestConnectProxy(auth));
        if (server is not null) foreach (var h in hosts) p.Map[h] = server.Port;
        return p;
    }

    private static NetworkContext Ctx(TestTlsServer trust, ProxyConfig cfg, ProxyEnvironment env) =>
        new(() => cfg, env, trust.TrustThisServer);

    private static ProxyEnvironment EnvWith(string? https, string? no = null) =>
        ProxyEnvironment.From([KeyValuePair.Create("HTTPS_PROXY", https), KeyValuePair.Create("NO_PROXY", no)]);

    private static async Task<string> GetString(NetworkContext net, string url, NetworkPurpose purpose = NetworkPurpose.ModelDownload, RedirectPolicy? policy = null)
    {
        using var client = net.CreateClient(purpose, policy, TimeSpan.FromSeconds(20));
        using var resp = await client.GetAsync(url);
        return $"{(int)resp.StatusCode}:{await resp.Content.ReadAsStringAsync()}";
    }

    [Fact]
    public async Task Environment_mode_routes_https_through_the_connect_proxy()
    {
        var server = Server();
        var proxy = Proxy(server, null, "models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));

        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
        var c = Assert.Single(proxy.Tunnelled);
        Assert.Equal(("models.test", 443), (c.Host, c.Port));
        Assert.Equal("models.test", server.Requests.Single().Host?.Split(':')[0]);
    }

    [Fact]
    public async Task Environment_mode_without_a_proxy_variable_goes_direct()
    {
        var server = Server();
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(null));
        Assert.Equal("200:hello", await GetString(net, $"https://127.0.0.1:{server.Port}/x"));
        Assert.False(net.Describe(new Uri("https://huggingface.co/")).ViaProxy);
    }

    [Fact]
    public async Task Loopback_is_always_direct_even_when_a_proxy_is_configured()
    {
        var server = Server();
        var proxy = Proxy(server, null, "127.0.0.1");
        foreach (var cfg in new[] { ProxyConfig.Environment, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null) })
        {
            var net = Ctx(server, cfg, EnvWith(proxy.Url));
            Assert.Equal("200:hello", await GetString(net, $"https://127.0.0.1:{server.Port}/x"));
        }
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task No_proxy_match_goes_direct_and_never_touches_the_proxy()
    {
        var server = Server();
        var proxy = Proxy(server, null, "models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url, "models.test"));
        Assert.False(net.Describe(new Uri("https://models.test/")).ViaProxy);
        // 直結になるので .test は名前解決できず失敗する。プロキシには来ない。
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task None_mode_ignores_environment_variables()
    {
        var server = Server();
        var proxy = Proxy(server, null, "models.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.None, "", "", null), EnvWith(proxy.Url));
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task Manual_mode_uses_credentials_and_does_not_mix_in_the_environment_proxy()
    {
        var server = Server();
        var manual = Proxy(server, "alice:s3cret", "models.test");
        var envProxy = Proxy(server, null, "models.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, manual.Url, "alice", "s3cret"), EnvWith(envProxy.Url));

        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
        Assert.NotEmpty(manual.Tunnelled);
        Assert.Contains(manual.Connects, c => c.ProxyAuthorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")));
        Assert.Empty(envProxy.Connects);
    }

    [Fact]
    public async Task Wrong_proxy_credentials_fail_and_never_fall_back_to_direct()
    {
        var server = Server();
        var proxy = Proxy(server, "alice:s3cret", "models.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "alice", "wrong"), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(server.Requests);
        Assert.True(proxy.AuthFailures > 0);
    }

    [Fact]
    public async Task Missing_credentials_for_an_authenticating_proxy_fail()
    {
        var server = Server();
        var proxy = Proxy(server, "alice:s3cret", "models.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Credentials_in_the_environment_proxy_url_are_used()
    {
        var server = Server();
        var proxy = Proxy(server, "bob:p@ss", "models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith($"http://bob:p%40ss@127.0.0.1:{proxy.Port}"));
        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
    }

    [Fact]
    public void Invalid_proxy_configuration_throws_instead_of_going_direct()
    {
        var server = Server();
        Assert.Throws<NetworkConfigurationException>(() => Ctx(server, ProxyConfig.Environment, EnvWith("socks5://x:1")).CreateClient(NetworkPurpose.Update));
        Assert.Throws<NetworkConfigurationException>(() => Ctx(server, new ProxyConfig(ProxyMode.Manual, "", "", null), ProxyEnvironment.Empty).CreateClient(NetworkPurpose.Update));
        Assert.Throws<NetworkConfigurationException>(() => Ctx(server, ProxyConfig.Environment, EnvWith("http://p:1", "10.0.0.0/8")).CreateClient(NetworkPurpose.Update));
    }

    [Fact]
    public async Task Unreachable_proxy_is_a_connection_error_not_a_direct_fallback()
    {
        var server = Server();
        var closed = Proxy(); closed.Dispose();
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, closed.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Settings_changes_apply_to_the_next_client_without_restart()
    {
        var server = Server();
        var proxy = Proxy(server, null, "models.test");
        var cfg = new ProxyConfig(ProxyMode.None, "", "", null);
        var net = new NetworkContext(() => cfg, ProxyEnvironment.Empty, server.TrustThisServer);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        cfg = new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null);
        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
    }

    // ---- HTTPS プロキシ（プロキシ自体への接続を TLS で行う）。信頼はテスト用 CA だけ。ホスト名の検証は実物と同じ ----

    private TestCa Ca() => Track(new TestCa());

    private TestConnectProxy TlsProxy(TestCa ca, TestTlsServer? server, string? auth = null, string? dns = null, IPAddress? ip = null, X509Certificate2? cert = null, params string[] hosts)
    {
        var p = Track(new TestConnectProxy(auth, cert ?? Track(ca.Issue(dns, ip ?? IPAddress.Loopback))));
        if (server is not null) foreach (var h in hosts) p.Map[h] = server.Port;
        return p;
    }

    private static NetworkContext CaCtx(TestCa ca, TestTlsServer server, ProxyConfig cfg, ProxyEnvironment env) =>
        new(() => cfg, env, ca.Trust(server));

    [Fact]
    public async Task Https_proxy_in_manual_mode_reaches_the_target_over_tls()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, hosts: "models.test");
        Assert.StartsWith("https://", proxy.Url);
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);

        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
        Assert.Equal(("models.test", 443), (proxy.Tunnelled.Single().Host, proxy.Tunnelled.Single().Port));
        Assert.Equal(0, proxy.TlsHandshakeFailures);
        Assert.True(net.Describe(new Uri("https://models.test/")).ViaProxy);
    }

    [Fact]
    public async Task Https_proxy_in_environment_mode_reaches_the_target_over_tls()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, hosts: "models.test");
        var net = CaCtx(ca, server, ProxyConfig.Environment, EnvWith(proxy.Url));
        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
        Assert.Single(proxy.Tunnelled);
    }

    [Fact]
    public async Task Https_proxy_serves_the_cleanup_path_too()
    {
        var ca = Ca(); var server = Server(_ => new TestResponse(200, Encoding.ASCII.GetBytes("polished")));
        var proxy = TlsProxy(ca, server, hosts: "api.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);
        using var client = net.CreateClient(NetworkPurpose.Cleanup);
        using var resp = await client.PostAsync("https://api.test/v1/chat/completions", new StringContent("{}"));
        Assert.Equal("polished", await resp.Content.ReadAsStringAsync());
        Assert.Single(proxy.Tunnelled);
    }

    [Fact]
    public async Task Https_proxy_with_correct_credentials_succeeds_and_sends_them_only_inside_tls()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, "alice:s3cret", hosts: "models.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "alice", "s3cret"), ProxyEnvironment.Empty);
        Assert.Equal("200:hello", await GetString(net, "https://models.test/file"));
        // フィクスチャーは TLS を終端した後の平文を見ている。Basic はその内側（CONNECT のヘッダー）にだけ現れる。
        Assert.Contains(proxy.Connects, c => c.ProxyAuthorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")));
    }

    [Fact]
    public async Task Https_proxy_with_wrong_credentials_fails_and_never_reaches_the_target()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, "alice:s3cret", hosts: "models.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "alice", "wrong"), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(server.Requests);
        Assert.True(proxy.AuthFailures > 0);
    }

    [Fact]
    public async Task Https_proxy_credentials_are_never_sent_when_the_certificate_is_untrusted()
    {
        var ca = Ca(); var other = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, "alice:s3cret", cert: other.Issue(null, IPAddress.Loopback), hosts: "models.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "alice", "s3cret"), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_proxy_with_a_self_signed_certificate_not_from_the_test_ca_is_an_error_not_a_direct_fallback()
    {
        var ca = Ca(); var server = Server();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=rogue", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var rogue = Track(new X509Certificate2(self.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable));
        var proxy = TlsProxy(ca, server, cert: rogue, hosts: "models.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);

        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_proxy_certificate_for_a_different_host_name_is_an_error()
    {
        var ca = Ca(); var server = Server();
        // CA 署名だが SAN は別名だけ（127.0.0.1 を含まない）
        var proxy = TlsProxy(ca, server, dns: "elsewhere.test", ip: IPAddress.IPv6Loopback, hosts: "models.test");
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_proxy_without_test_trust_is_rejected_by_the_default_validation()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, hosts: "models.test");
        var net = new NetworkContext(() => new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task Unreachable_https_proxy_is_a_connection_error_not_a_direct_fallback()
    {
        var ca = Ca(); var server = Server();
        var closed = TlsProxy(ca, null); closed.Dispose();
        var net = CaCtx(ca, server, new ProxyConfig(ProxyMode.Manual, closed.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_proxy_does_not_change_loopback_and_no_proxy_rules()
    {
        var ca = Ca(); var server = Server();
        var proxy = TlsProxy(ca, server, hosts: "127.0.0.1");
        var net = CaCtx(ca, server, ProxyConfig.Environment, EnvWith(proxy.Url));
        Assert.Equal("200:hello", await GetString(net, $"https://127.0.0.1:{server.Port}/x"));
        Assert.Empty(proxy.Connects);
        var net2 = CaCtx(ca, server, ProxyConfig.Environment, EnvWith(proxy.Url, "models.test"));
        Assert.False(net2.Describe(new Uri("https://models.test/")).ViaProxy);
    }

    [Fact]
    public async Task Untrusted_certificates_are_rejected_when_no_test_trust_is_given()
    {
        var server = Server();
        var proxy = Proxy(server, null, "models.test");
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(proxy.Url)); // 本番の入口: OS の信頼ストアのみ
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GetString(net, "https://models.test/file"));
        Assert.Contains(proxy.Connects, c => c.Host == "models.test"); // プロキシまでは到達し、TLS で拒否された
        Assert.IsType<System.Security.Authentication.AuthenticationException>(ex.InnerException);
        Assert.Empty(server.Requests);
    }

    // ---- リダイレクト ----

    private static readonly RedirectPolicy TestPolicy = new(u => u.Host.EndsWith(".test") || u.Host == "127.0.0.1");

    [Fact]
    public async Task Redirects_are_followed_per_hop_and_the_route_is_recomputed()
    {
        TestTlsServer? cdn = null;
        var origin = Server(r => r.Target == "/a" ? TestResponse.Redirect($"https://127.0.0.1:{cdn!.Port}/b") : new TestResponse(404, []));
        cdn = Server(_ => new TestResponse(200, Encoding.ASCII.GetBytes("payload")));
        var proxy = Proxy(origin, null, "models.test");
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(proxy.Url),
            o => o.RemoteCertificateValidationCallback = (_, _, _, _) => true); // 2 台の fixture 証明書を許可（テスト専用）

        Assert.Equal("200:payload", await GetString(net, "https://models.test/a", policy: TestPolicy));
        // 1 hop 目はプロキシ経由、2 hop 目（loopback）は直結。
        Assert.Equal(["models.test"], proxy.Tunnelled.Select(c => c.Host));
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task Redirect_chain_through_the_proxy_records_every_hop()
    {
        var server = Server(r => r.Target switch
        {
            "/a" => TestResponse.Redirect("https://cdn.models.test/b", 301),
            "/b" => TestResponse.Redirect("/c", 307),
            _ => new TestResponse(200, Encoding.ASCII.GetBytes("done")),
        });
        var proxy = Proxy(server, null, "models.test", "cdn.models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        Assert.Equal("200:done", await GetString(net, "https://models.test/a", policy: TestPolicy));
        Assert.Equal(["models.test", "cdn.models.test"], proxy.Tunnelled.Select(c => c.Host).Distinct());
    }

    [Fact]
    public async Task Redirect_to_a_host_outside_the_allowlist_is_refused()
    {
        var server = Server(_ => TestResponse.Redirect("https://evil.example/x"));
        var proxy = Proxy(server, null, "models.test", "evil.example");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        var ex = await Assert.ThrowsAsync<NetworkPolicyException>(() => GetString(net, "https://models.test/a", policy: TestPolicy));
        Assert.Contains("evil.example", ex.Message);
        Assert.DoesNotContain(proxy.Connects, c => c.Host == "evil.example");
    }

    [Fact]
    public async Task Redirect_to_plain_http_is_refused()
    {
        var server = Server(_ => TestResponse.Redirect("http://cdn.models.test/x"));
        var proxy = Proxy(server, null, "models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        await Assert.ThrowsAsync<NetworkPolicyException>(() => GetString(net, "https://models.test/a", policy: TestPolicy));
    }

    [Fact]
    public async Task More_than_five_redirects_are_refused()
    {
        var server = Server(r => TestResponse.Redirect("https://models.test/next" + r.Target.Length));
        var proxy = Proxy(server, null, "models.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        var ex = await Assert.ThrowsAsync<NetworkPolicyException>(() => GetString(net, "https://models.test/a", policy: TestPolicy));
        Assert.Contains("5", ex.Message);
        Assert.Equal(6, server.Requests.Count); // 初回 + 5 hop
    }

    [Fact]
    public async Task Authorization_is_not_forwarded_to_another_host_on_redirect()
    {
        var other = Server(_ => new TestResponse(200, []));
        var first = Server(_ => TestResponse.Redirect("https://other.models.test/x"));
        var proxy = Proxy(null, null);
        proxy.Map["first.models.test"] = first.Port;
        proxy.Map["other.models.test"] = other.Port;
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(proxy.Url), o => o.RemoteCertificateValidationCallback = (_, _, _, _) => true);
        using var client = net.CreateClient(NetworkPurpose.Update, TestPolicy);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://first.models.test/x");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer SECRET");
        using var resp = await client.SendAsync(req);
        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Equal("Bearer SECRET", first.Requests.Single().Headers["Authorization"]);
        Assert.DoesNotContain("Authorization", other.Requests.Single().Headers.Keys);
    }

    [Fact]
    public async Task Cleanup_requests_never_follow_redirects()
    {
        var target = Server();
        var server = Server(_ => TestResponse.Redirect($"https://127.0.0.1:{target.Port}/leak"));
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(null), o => o.RemoteCertificateValidationCallback = (_, _, _, _) => true);
        using var client = net.CreateClient(NetworkPurpose.Cleanup);
        using var resp = await client.PostAsync($"https://127.0.0.1:{server.Port}/chat", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Empty(target.Requests);
    }

    [Theory]
    [InlineData(NetworkPurpose.ModelDownload, "http://localhost:1/x")]
    [InlineData(NetworkPurpose.Update, "http://localhost:1/x")]
    [InlineData(NetworkPurpose.Cleanup, "http://example.com/x")]
    [InlineData(NetworkPurpose.Cleanup, "ftp://example.com/x")]
    public async Task Non_https_destinations_are_rejected_by_policy(NetworkPurpose purpose, string url)
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Empty);
        using var client = net.CreateClient(purpose);
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.GetAsync(url));
    }

    [Fact]
    public async Task Cleanup_may_use_plain_http_only_to_loopback()
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith("http://127.0.0.1:1"));
        using var client = net.CreateClient(NetworkPurpose.Cleanup);
        // スキーム規則は通り、接続できずに失敗する（ポリシー違反ではない）。プロキシ（到達不能）も経由しない。
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:1/v1"));
    }

    [Fact]
    public void Environment_display_shows_only_the_host_and_port_without_credentials()
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith("http://bob:secret@proxy.corp.example:8080"));
        Assert.Equal("proxy.corp.example:8080", net.EnvironmentProxyDisplay());
    }

    [Fact]
    public void Proxy_config_from_settings_decrypts_the_manual_password_and_fails_loudly_if_it_cannot()
    {
        var p = new Whisparrot.Infrastructure.Settings.DpapiSecretProtector();
        var ok = ProxyConfig.FromSettings(new ProxySettings(ProxyMode.Manual, "http://p:1", "u", p.Protect("pw")), p);
        Assert.Equal("pw", ok.ManualPassword);
        Assert.Throws<NetworkConfigurationException>(() => ProxyConfig.FromSettings(new ProxySettings(ProxyMode.Manual, "http://p:1", "u", "AAAA"), p));
        // manual 以外ではパスワードを使わない（復号も試みない）
        Assert.Null(ProxyConfig.FromSettings(new ProxySettings(ProxyMode.None, "", "u", "AAAA"), p).ManualPassword);
    }
}
