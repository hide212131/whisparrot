using System.Net;
using System.Net.Sockets;
using System.Text;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

/// <summary>
/// NetworkPurpose.Transcription（クラウド文字起こし設計 §8）。音声 multipart の代わりに小さな POST 本文を使い、policy・転送・proxy・TLS を確かめる。
/// ループバックの TLS サーバーと記録用 CONNECT プロキシによる結合試験で、実際の外部サービス・実 proxy 環境ではない。
/// </summary>
public sealed class TranscriptionNetworkTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private T Track<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

    private TestTlsServer Server(Func<TestRequest, TestResponse>? handler = null) =>
        Track(new TestTlsServer(handler ?? (_ => new TestResponse(200, Encoding.ASCII.GetBytes("{\"text\":\"ok\"}")))));

    private TestConnectProxy Proxy(TestTlsServer? server = null, string? auth = null, params string[] hosts)
    {
        var p = Track(new TestConnectProxy(auth));
        if (server is not null) foreach (var h in hosts) p.Map[h] = server.Port;
        return p;
    }

    private static NetworkContext Ctx(TestTlsServer trust, ProxyConfig cfg, ProxyEnvironment env) => new(() => cfg, env, trust.TrustThisServer);

    private static ProxyEnvironment EnvWith(string? https, string? no = null) =>
        ProxyEnvironment.From([KeyValuePair.Create("HTTPS_PROXY", https), KeyValuePair.Create("NO_PROXY", no)]);

    private static async Task<HttpResponseMessage> Post(NetworkContext net, string url, RedirectPolicy? policy = null, string? bearer = null)
    {
        using var client = net.CreateClient(NetworkPurpose.Transcription, policy, TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("audio-bytes") };
        if (bearer is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return await client.SendAsync(req);
    }

    // ---- 既定の期限 ----

    [Fact]
    public void Default_timeout_is_60_seconds_and_existing_purposes_keep_theirs()
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Empty);
        using (var t = net.CreateClient(NetworkPurpose.Transcription)) Assert.Equal(TimeSpan.FromSeconds(60), t.Timeout);
        using (var c = net.CreateClient(NetworkPurpose.Cleanup)) Assert.Equal(TimeSpan.FromSeconds(15), c.Timeout);
        using (var u = net.CreateClient(NetworkPurpose.Update)) Assert.Equal(TimeSpan.FromSeconds(30), u.Timeout);
        using (var m = net.CreateClient(NetworkPurpose.ModelDownload)) Assert.Equal(Timeout.InfiniteTimeSpan, m.Timeout);
        using (var x = net.CreateClient(NetworkPurpose.Transcription, timeout: Timeout.InfiniteTimeSpan)) Assert.Equal(Timeout.InfiniteTimeSpan, x.Timeout);
    }

    // ---- スキーム（HTTP は正確な loopback の3形だけ）----

    [Theory]
    [InlineData("http://example.com/v1/audio/transcriptions")]
    [InlineData("http://127.0.0.2:1/x")]            // 127.0.0.0/8 全体には広げない（Cleanup の広い判定は流用しない）
    [InlineData("http://0.0.0.0:1/x")]
    [InlineData("http://[::ffff:127.0.0.1]:1/x")]
    [InlineData("http://localhost.example.com:1/x")]
    [InlineData("ftp://localhost:1/x")]
    public async Task Plain_http_is_refused_except_to_the_exact_loopback_forms(string url)
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Empty);
        using var client = net.CreateClient(NetworkPurpose.Transcription);
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.PostAsync(url, new StringContent("x")));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("LOCALHOST")]
    public async Task Exact_loopback_http_passes_the_policy_and_goes_direct_even_with_a_proxy(string host)
    {
        using var plain = new PlainHttpServer();
        var proxy = Proxy();
        var net = new NetworkContext(() => new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), EnvWith(proxy.Url));
        using var client = net.CreateClient(NetworkPurpose.Transcription, timeout: TimeSpan.FromSeconds(20));
        using var resp = await client.PostAsync($"http://{host}:{plain.Port}/v1/audio/transcriptions", new StringContent("x"));
        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Equal(1, plain.Count);
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task Exact_loopback_ipv6_http_passes_the_policy()
    {
        PlainHttpServer plain;
        try { plain = new PlainHttpServer(IPAddress.IPv6Loopback); }
        catch (SocketException) { return; } // IPv6 loopback が無い環境
        using (plain)
        {
            var net = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Empty);
            using var client = net.CreateClient(NetworkPurpose.Transcription, timeout: TimeSpan.FromSeconds(20));
            using var resp = await client.PostAsync($"http://[::1]:{plain.Port}/x", new StringContent("x"));
            Assert.Equal(200, (int)resp.StatusCode);
        }
    }

    // ---- 転送拒否 ----

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_are_never_followed_even_when_the_caller_passes_a_policy(int status)
    {
        var target = Server();
        var server = Server(_ => TestResponse.Redirect($"https://127.0.0.1:{target.Port}/leak", status));
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(null), o =>
            o.RemoteCertificateValidationCallback = (_, _, _, _) => true);
        var allowAll = new RedirectPolicy(_ => true);

        foreach (var policy in new RedirectPolicy?[] { null, allowAll })
        {
            using var resp = await Post(net, $"https://127.0.0.1:{server.Port}/v1/audio/transcriptions", policy, bearer: "DUMMY-KEY");
            Assert.Equal(status, (int)resp.StatusCode);
        }
        Assert.Empty(target.Requests);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Redirect_following_is_limited_to_update_and_model_download()
    {
        // 明示列挙: Update / ModelDownload は従来どおり追う。Cleanup / Transcription は追わない。
        foreach (var (purpose, follows) in new[]
        {
            (NetworkPurpose.Update, true), (NetworkPurpose.ModelDownload, true), (NetworkPurpose.Cleanup, false), (NetworkPurpose.Transcription, false),
        })
        {
            var target = Server();
            var server = Server(_ => TestResponse.Redirect($"https://127.0.0.1:{target.Port}/next"));
            var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(null), o => o.RemoteCertificateValidationCallback = (_, _, _, _) => true);
            using var client = net.CreateClient(purpose, new RedirectPolicy(_ => true), TimeSpan.FromSeconds(20));
            using var resp = await client.GetAsync($"https://127.0.0.1:{server.Port}/start");
            Assert.Equal(follows, target.Requests.Count == 1);
            Assert.Equal(follows ? HttpStatusCode.OK : HttpStatusCode.Found, resp.StatusCode);
        }
    }

    // ---- 3 proxy モード ----

    [Fact]
    public async Task Environment_mode_routes_through_the_connect_proxy_and_bearer_goes_only_to_the_api()
    {
        var server = Server();
        var proxy = Proxy(server, null, "api.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        using var resp = await Post(net, "https://api.test/v1/audio/transcriptions", bearer: "DUMMY-KEY");
        Assert.Equal(200, (int)resp.StatusCode);
        var c = Assert.Single(proxy.Tunnelled);
        Assert.Equal(("api.test", 443), (c.Host, c.Port));
        Assert.Null(c.ProxyAuthorization);                                   // Bearer は proxy へ流れない
        Assert.Equal("Bearer DUMMY-KEY", server.Requests.Single().Headers["Authorization"]);
    }

    [Fact]
    public async Task Environment_mode_without_a_proxy_variable_goes_direct()
    {
        var server = Server();
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(null));
        using var resp = await Post(net, $"https://127.0.0.1:{server.Port}/x");
        Assert.Equal(200, (int)resp.StatusCode);
    }

    [Fact]
    public async Task No_proxy_match_goes_direct_and_never_touches_the_proxy()
    {
        var server = Server();
        var proxy = Proxy(server, null, "api.test");
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url, "api.test"));
        Assert.False(net.Describe(new Uri("https://api.test/")).ViaProxy);
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net, "https://api.test/v1/audio/transcriptions"));
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task None_mode_ignores_environment_variables()
    {
        var server = Server();
        var proxy = Proxy(server, null, "api.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.None, "", "", null), EnvWith(proxy.Url));
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net, "https://api.test/x"));
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task Loopback_https_is_direct_even_when_a_proxy_is_configured()
    {
        var server = Server();
        var proxy = Proxy(server, null, "127.0.0.1");
        foreach (var cfg in new[] { ProxyConfig.Environment, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null) })
        {
            using var resp = await Post(Ctx(server, cfg, EnvWith(proxy.Url)), $"https://127.0.0.1:{server.Port}/x");
            Assert.Equal(200, (int)resp.StatusCode);
        }
        Assert.Empty(proxy.Connects);
    }

    [Fact]
    public async Task Manual_mode_sends_credentials_to_the_proxy_only_and_keeps_bearer_for_the_api()
    {
        var server = Server();
        var manual = Proxy(server, "alice:s3cret", "api.test");
        var envProxy = Proxy(server, null, "api.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, manual.Url, "alice", "s3cret"), EnvWith(envProxy.Url));
        using var resp = await Post(net, "https://api.test/v1/audio/transcriptions", bearer: "DUMMY-KEY");
        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Contains(manual.Connects, c => c.ProxyAuthorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")));
        Assert.Empty(envProxy.Connects);
        var api = server.Requests.Single();
        Assert.Equal("Bearer DUMMY-KEY", api.Headers["Authorization"]);        // proxy 資格情報は API へ流れない
        Assert.DoesNotContain("alice", string.Join("\n", api.Headers.Values));
    }

    [Fact]
    public async Task Wrong_proxy_credentials_fail_without_going_direct()
    {
        var server = Server();
        var proxy = Proxy(server, "alice:s3cret", "api.test");
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "alice", "wrong"), ProxyEnvironment.Empty);
        // 407 は HttpRequestException。黙って直結せず、API には何も届かない。
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net, "https://api.test/v1/audio/transcriptions"));
        Assert.Empty(server.Requests);
        Assert.True(proxy.AuthFailures > 0);
    }

    [Fact]
    public void Invalid_proxy_configuration_throws_instead_of_going_direct()
    {
        var server = Server();
        Assert.Throws<NetworkConfigurationException>(() => Ctx(server, ProxyConfig.Environment, EnvWith("socks5://x:1")).CreateClient(NetworkPurpose.Transcription));
        Assert.Throws<NetworkConfigurationException>(() => Ctx(server, new ProxyConfig(ProxyMode.Manual, "", "", null), ProxyEnvironment.Empty).CreateClient(NetworkPurpose.Transcription));
    }

    [Fact]
    public async Task Unreachable_proxy_is_a_connection_error_not_a_direct_fallback()
    {
        var server = Server();
        var closed = Proxy(); closed.Dispose();
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, closed.Url, "", null), ProxyEnvironment.Empty);
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net, "https://api.test/x"));
        Assert.Empty(server.Requests);
    }

    // ---- Windows 統合認証（SSH セッションでの実行が前提。docs/m5-report.md）----

    private (TestTlsServer Server, TestConnectProxy Proxy, IntegratedAuthScript Script) AuthSetup(ChallengeMode mode)
    {
        var server = Server();
        var script = new IntegratedAuthScript(mode, true);
        var proxy = Track(new TestConnectProxy { IntegratedAuth = script });
        proxy.Map["api.test"] = server.Port;
        return (server, proxy, script);
    }

    [Fact]
    public async Task Environment_mode_answers_an_ntlm_proxy_challenge_with_the_windows_logon_for_a_transcription_post()
    {
        var (server, proxy, script) = AuthSetup(ChallengeMode.Ntlm);
        var net = Ctx(server, ProxyConfig.Environment, EnvWith(proxy.Url));
        using var resp = await Post(net, "https://api.test/v1/audio/transcriptions", bearer: "DUMMY-KEY");
        Assert.Equal(200, (int)resp.StatusCode);
        Assert.Equal(3, script.Events.Count);
        Assert.Equal("none", script.Events[0]);
        Assert.Equal("ntlm:type1", script.Events[1]);
        Assert.StartsWith("ntlm:type3(", script.Events[2]);
        var api = Assert.Single(server.Requests);
        Assert.Equal("Bearer DUMMY-KEY", api.Headers["Authorization"]);
    }

    [Fact]
    public async Task Manual_windows_auth_ignores_the_username_field_and_a_rejected_handshake_never_goes_direct()
    {
        var (server, proxy, script) = AuthSetup(ChallengeMode.Ntlm);
        var net = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "ignored-user", "ignored-pw", ProxyManualAuth.Windows), ProxyEnvironment.Empty);
        using (var resp = await Post(net, "https://api.test/x")) Assert.Equal(200, (int)resp.StatusCode);
        Assert.DoesNotContain("user=24", script.Events[2]);

        var (server2, proxy2, script2) = (Server(), (TestConnectProxy?)null, new IntegratedAuthScript(ChallengeMode.Ntlm, false));
        proxy2 = Track(new TestConnectProxy { IntegratedAuth = script2 });
        proxy2.Map["api.test"] = server2.Port;
        var net2 = Ctx(server2, ProxyConfig.Environment, EnvWith(proxy2.Url));
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net2, "https://api.test/x"));
        Assert.Empty(server2.Requests);
    }

    // ---- TLS ----

    [Fact]
    public async Task Untrusted_certificates_are_rejected_when_no_test_trust_is_given()
    {
        var server = Server();
        var net = new NetworkContext(() => ProxyConfig.Environment, EnvWith(null));
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(net, $"https://127.0.0.1:{server.Port}/x"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Https_proxy_serves_the_transcription_path_and_an_untrusted_proxy_certificate_is_an_error()
    {
        using var ca = new TestCa();
        var server = Server();
        var proxy = Track(new TestConnectProxy(null, Track(ca.Issue(null, IPAddress.Loopback))));
        proxy.Map["api.test"] = server.Port;
        var trusted = new NetworkContext(() => new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty, ca.Trust(server));
        using (var resp = await Post(trusted, "https://api.test/v1/audio/transcriptions")) Assert.Equal(200, (int)resp.StatusCode);

        var untrusted = Ctx(server, new ProxyConfig(ProxyMode.Manual, proxy.Url, "", null), ProxyEnvironment.Empty); // proxy 証明書を信頼しない
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(untrusted, "https://api.test/v1/audio/transcriptions"));
        Assert.Single(server.Requests);
    }

    /// <summary>Content-Length 付きで本文を読み、200 を返すだけの平文 HTTP サーバー（loopback HTTP の policy 試験用）。</summary>
    private sealed class PlainHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _count;

        public PlainHttpServer(IPAddress? address = null)
        {
            _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Loop);
        }

        public int Port { get; }
        public int Count => Volatile.Read(ref _count);

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var tcp = c;
                    try
                    {
                        var s = tcp.GetStream();
                        var head = await HttpText.ReadHead(s, _cts.Token);
                        if (head is null) return;
                        var req = HttpText.Parse(head);
                        await HttpText.ReadBody(s, req, _cts.Token);
                        Interlocked.Increment(ref _count);
                        var body = Encoding.ASCII.GetBytes("{\"text\":\"ok\"}");
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await s.WriteAsync(body);
                    }
                    catch { }
                });
            }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }
}
