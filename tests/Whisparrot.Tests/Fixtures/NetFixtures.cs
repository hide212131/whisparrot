using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Whisparrot.Tests.Fixtures;

internal sealed record TestRequest(string Method, string Target, string? Host, IReadOnlyDictionary<string, string> Headers, byte[]? Body = null)
{
    public string BodyText => Body is null ? "" : Encoding.UTF8.GetString(Body);
}

/// <summary>Body は <see cref="HoldAfter"/> バイトを送った後、<see cref="Release"/> が完了するまで止める（取消・同時取得の試験用）。</summary>
internal sealed record TestResponse(int Status, byte[] Body, Dictionary<string, string>? Headers = null, int? ContentLength = null, int? HoldAfter = null, Task? Release = null)
{
    public static TestResponse Redirect(string location, int status = 302) => new(status, [], new() { ["Location"] = location });
}

/// <summary>自己署名証明書の HTTPS サーバー（ループバック）。Connection: close の最小実装。</summary>
internal sealed class TestTlsServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<TestRequest, TestResponse> _handler;
    private readonly object _lock = new();

    public TestTlsServer(Func<TestRequest, TestResponse> handler)
    {
        _handler = handler;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=whisparrot-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Certificate = new X509Certificate2(self.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public X509Certificate2 Certificate { get; }
    public int Port { get; }
    public List<TestRequest> Requests { get; } = [];

    /// <summary>このサーバーの証明書だけを受け入れる検証（テスト専用の入口へ渡す）。</summary>
    public Action<SslClientAuthenticationOptions> TrustThisServer => o =>
        o.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == Certificate.GetCertHashString();

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                await using var ssl = new SslStream(tcp.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(Certificate);
                var head = await HttpText.ReadHead(ssl, _cts.Token);
                if (head is null) return;
                var req = HttpText.Parse(head);
                req = req with { Body = await HttpText.ReadBody(ssl, req, _cts.Token) };
                lock (_lock) Requests.Add(req);
                var r = _handler(req);
                var sb = new StringBuilder($"HTTP/1.1 {r.Status} X\r\nContent-Length: {r.ContentLength ?? r.Body.Length}\r\nConnection: close\r\n");
                foreach (var (k, v) in r.Headers ?? []) sb.Append($"{k}: {v}\r\n");
                sb.Append("\r\n");
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                if (r.HoldAfter is { } n && n < r.Body.Length)
                {
                    await ssl.WriteAsync(r.Body.AsMemory(0, n));
                    await ssl.FlushAsync();
                    if (r.Release is not null) await Task.WhenAny(r.Release, Task.Delay(30000, _cts.Token));
                    await ssl.WriteAsync(r.Body.AsMemory(n));
                }
                else await ssl.WriteAsync(r.Body);
                await ssl.FlushAsync();
            }
            catch { /* クライアントが取消・切断した */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        Certificate.Dispose();
    }
}

internal sealed record ConnectRecord(string Host, int Port, string? ProxyAuthorization);

/// <summary>記録用の CONNECT プロキシ。ホスト名は Map でループバックのポートへ解決する（クライアントは名前解決しない）。</summary>
internal sealed class TestConnectProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    private readonly X509Certificate2? _tls;

    /// <param name="tls">指定すると、プロキシ自体への接続を TLS で受ける（HTTPS プロキシ）。</param>
    public TestConnectProxy(string? requireBasic = null, X509Certificate2? tls = null)
    {
        RequireBasic = requireBasic;
        _tls = tls;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>"user:password"（Basic）。null なら認証不要。</summary>
    public string? RequireBasic { get; }

    /// <summary>統合認証（NTLM / Negotiate）のチャレンジを返すスクリプト。指定すると <see cref="RequireBasic"/> より先に適用する。</summary>
    public IntegratedAuthScript? IntegratedAuth { get; init; }
    public int Port { get; }
    public Dictionary<string, int> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ConnectRecord> Connects { get; } = [];
    public int AuthFailures { get; private set; }
    public int TlsHandshakeFailures { get; private set; }
    public string Url => $"{(_tls is null ? "http" : "https")}://127.0.0.1:{Port}";

    public IReadOnlyList<ConnectRecord> Tunnelled { get { lock (_lock) return Connects.Where(c => Map.ContainsKey(c.Host)).ToList(); } }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_tls is not null)
                {
                    var ssl = new SslStream(stream, false);
                    try { await ssl.AuthenticateAsServerAsync(_tls); }
                    catch { lock (_lock) TlsHandshakeFailures++; return; }
                    stream = ssl;
                }
                var integrated = IntegratedAuth?.NewConnection();
                while (true)
                {
                    var head = await HttpText.ReadHead(stream, _cts.Token);
                    if (head is null) return;
                    var req = HttpText.Parse(head);
                    if (req.Method != "CONNECT") { await Write(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"); return; }
                    var parts = req.Target.Split(':');
                    var host = parts[0];
                    var port = int.Parse(parts[1]);
                    req.Headers.TryGetValue("Proxy-Authorization", out var auth);
                    lock (_lock) Connects.Add(new ConnectRecord(host, port, auth));

                    if (integrated is not null)
                    {
                        var step = integrated(auth);
                        if (!step.Accept)
                        {
                            AuthFailures++;
                            var challenge = string.Concat(step.Challenges.Select(c => $"Proxy-Authenticate: {c}\r\n"));
                            await Write(stream, $"HTTP/1.1 407 Proxy Authentication Required\r\n{challenge}Content-Length: 0\r\n\r\n");
                            if (step.Close) return;
                            continue;
                        }
                    }
                    else if (RequireBasic is not null && auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(RequireBasic)))
                    {
                        AuthFailures++;
                        await Write(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                        continue; // 同じ接続で認証付きの再送を待つ
                    }
                    if (!Map.TryGetValue(host, out var target)) { await Write(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"); return; }

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, target);
                    await Write(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                    var up = upstream.GetStream();
                    await Task.WhenAny(stream.CopyToAsync(up, _cts.Token), up.CopyToAsync(stream, _cts.Token));
                    return;
                }
            }
            catch { }
        }
    }

    private static Task Write(Stream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose() { _cts.Cancel(); _listener.Stop(); }
}

internal static class HttpText
{
    public static async Task<string?> ReadHead(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await s.ReadAsync(buf, ct) == 0) return null;
            sb.Append((char)buf[0]);
            if (sb.Length > 65536) return null;
        }
        return sb.ToString();
    }

    /// <summary>Content-Length 分の本文を読む（無ければ null）。</summary>
    public static async Task<byte[]?> ReadBody(Stream s, TestRequest req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("Content-Length", out var len) || !int.TryParse(len, out var n)) return null;
        var buf = new byte[n];
        var read = 0;
        while (read < n)
        {
            var m = await s.ReadAsync(buf.AsMemory(read), ct);
            if (m == 0) break;
            read += m;
        }
        return buf;
    }

    public static TestRequest Parse(string head)
    {
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i]] = l[(i + 1)..].Trim();
        }
        headers.TryGetValue("Host", out var host);
        return new TestRequest(first[0], first[1], host, headers);
    }
}

/// <summary>テスト用 CA と、そこから発行する葉証明書（HTTPS プロキシの検証用）。</summary>
internal sealed class TestCa : IDisposable
{
    private readonly X509Certificate2 _ca;

    public TestCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=whisparrot-test-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _ca = new X509Certificate2(self.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>SAN に <paramref name="dns"/> / <paramref name="ip"/> を持つサーバー証明書を発行する。</summary>
    public X509Certificate2 Issue(string? dns, IPAddress? ip)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=whisparrot-test-proxy", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        if (dns is not null) san.AddDnsName(dns);
        if (ip is not null) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var leaf = req.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(12), serial);
        using var withKey = leaf.CopyWithPrivateKey(rsa);
        return new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// このテスト用 CA を信頼の起点にして、ホスト名の一致を含め通常どおり検証する（テスト専用の入口へ渡す）。
    /// <paramref name="alsoTrust"/> は対象サーバー（自己署名）をハッシュで受け入れるための既存の方式。
    /// </summary>
    public Action<SslClientAuthenticationOptions> Trust(TestTlsServer? alsoTrust = null) => o =>
        o.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
        {
            if (cert is null) return false;
            if (alsoTrust is not null && cert.GetCertHashString() == alsoTrust.Certificate.GetCertHashString()) return true;
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(cert));
        };

    public void Dispose() => _ca.Dispose();
}
