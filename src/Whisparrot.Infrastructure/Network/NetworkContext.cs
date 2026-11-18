using System.Net;
using System.Net.Security;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Network;

/// <summary>
/// 全アプリ管理通信の用途（設計 §9.1、クラウド文字起こし設計 §8）。これ以外の通信を作らない。
/// <c>Transcription</c> はクラウド選択時の音声 multipart POST だけ。転送なし・60 秒・HTTPS（正確な loopback のみ HTTP）。
/// </summary>
public enum NetworkPurpose { Cleanup, Update, ModelDownload, Transcription }

/// <summary>プロキシ設定の実効値（パスワードは復号済みの平文でメモリ上のみ）。</summary>
public sealed record ProxyConfig(ProxyMode Mode, string ManualUrl, string ManualUsername, string? ManualPassword, ProxyManualAuth ManualAuth = ProxyManualAuth.Credentials)
{
    public static ProxyConfig Environment { get; } = new(ProxyMode.Environment, "", "", null);

    public static ProxyConfig FromSettings(ProxySettings s, ISecretProtector protector)
    {
        string? password = null;
        if (s.Mode == ProxyMode.Manual && s.ManualAuth == ProxyManualAuth.Credentials && !protector.TryUnprotect(s.ManualPasswordDpapi, out password))
            throw new NetworkConfigurationException("手動プロキシのパスワードを復号できません。設定で再入力してください");
        return new ProxyConfig(s.Mode, s.ManualUrl, s.ManualUsername, password, s.ManualAuth);
    }
}

/// <summary>リダイレクトの許可条件。GET/HEAD の HTTPS だけを最大 <see cref="MaxHops"/> 回まで追う。</summary>
public sealed record RedirectPolicy(Func<Uri, bool> IsHostAllowed, int MaxHops = 5)
{
    public static RedirectPolicy HuggingFace { get; } = new(u => HostIs(u, "huggingface.co") || HostIs(u, "hf.co"));

    public static RedirectPolicy GitHub { get; } = new(u =>
        HostIs(u, "github.com") || HostIs(u, "githubusercontent.com"));

    private static bool HostIs(Uri u, string domain) =>
        string.Equals(u.Host, domain, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}

/// <summary>経路の説明（設定画面・診断用）。</summary>
public sealed record RouteDescription(bool ViaProxy, string? ProxyHostPort, ProxyAuthKind Auth = ProxyAuthKind.None);

/// <summary>
/// 設計 §9.1 の共通生成口。経路はクライアント生成の時点の設定で決まる（設定変更は次の生成から効く。クライアントを使い回さないこと）。
/// 自動リダイレクトは無効にし、必要な用途だけが自前のハンドラーで各 hop を検証して追う。TLS 検証は OS の信頼ストアで、無効化の選択肢はない。
/// </summary>
public sealed class NetworkContext
{
    private readonly Func<ProxyConfig> _config;
    private readonly ProxyEnvironment _environment;
    private readonly Action<SslClientAuthenticationOptions>? _tlsTestHook;

    public NetworkContext(Func<ProxyConfig> config, ProxyEnvironment environment) : this(config, environment, null) { }

    /// <summary>テスト専用の入口（自己署名の fixture 証明書を受け入れるため）。製品コードからは使わない。</summary>
    internal NetworkContext(Func<ProxyConfig> config, ProxyEnvironment environment, Action<SslClientAuthenticationOptions>? tlsTestHook)
    {
        _config = config;
        _environment = environment;
        _tlsTestHook = tlsTestHook;
    }

    public ProxyEnvironment Environment => _environment;

    /// <summary>現在の設定での経路。不正なら <see cref="NetworkConfigurationException"/>。</summary>
    public RouteDescription Describe(Uri destination)
    {
        var (proxy, _, _) = Resolve();
        return proxy.IsBypassed(destination)
            ? new RouteDescription(false, null)
            : new RouteDescription(true, new ProxyTarget(proxy.GetProxy(destination)!, null).HostPort, proxy.AuthKind);
    }

    /// <summary>設定画面向け: 環境変数モードで効く（正規化後の）プロキシのホストだけ。</summary>
    public string? EnvironmentProxyDisplay()
    {
        try { return _environment.HttpsProxy is null ? null : ProxyTarget.Parse(_environment.HttpsProxy, "HTTPS_PROXY").HostPort; }
        catch (NetworkConfigurationException ex) { return "（不正: " + ex.Message + "）"; }
    }

    private (RoutingProxy Proxy, bool UseProxy, ProxyConfig Config) Resolve()
    {
        var cfg = _config();
        switch (cfg.Mode)
        {
            case ProxyMode.None:
                return (new RoutingProxy(null, NoProxyList.Empty), false, cfg);
            case ProxyMode.Manual:
            {
                var t = ProxyTarget.Parse(cfg.ManualUrl, "手動プロキシ");
                // 認証方式は排他: Windows ならログオン資格情報だけ（ユーザー名・パスワードは使わない）。
                if (cfg.ManualAuth == ProxyManualAuth.Windows)
                    t = t with { Credential = ProxyTarget.WindowsLogon };
                else if (cfg.ManualUsername.Length > 0)
                    t = t with { Credential = new NetworkCredential(cfg.ManualUsername, cfg.ManualPassword ?? "") };
                // 手動: 環境変数を混ぜない。NO_PROXY は適用しない（loopback のみ直結）。
                return (new RoutingProxy(t, NoProxyList.Empty), true, cfg);
            }
            default:
            {
                if (_environment.HttpsProxy is null) return (new RoutingProxy(null, NoProxyList.Empty), false, cfg);
                var t = ProxyTarget.Parse(_environment.HttpsProxy, "HTTPS_PROXY");
                // userinfo があればそれを優先。無ければ、ブラウザー・CLI と同じく OS のログオン資格情報で統合認証に応答する。
                t = t with { Credential = t.Credential ?? ProxyTarget.WindowsLogon };
                return (new RoutingProxy(t, new NoProxyList(_environment.NoProxy)), true, cfg);
            }
        }
    }

    public HttpClient CreateClient(NetworkPurpose purpose, RedirectPolicy? redirect = null, TimeSpan? timeout = null)
    {
        var (proxy, useProxy, _) = Resolve();
        var sockets = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = useProxy ? proxy : null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (_tlsTestHook is { } hook)
        {
            sockets.SslOptions = new SslClientAuthenticationOptions();
            hook(sockets.SslOptions);
        }

        // 転送を追うのは Update / ModelDownload だけ（明示列挙）。Cleanup / Transcription は呼出し側が policy を渡しても追わない。
        var effective = purpose switch
        {
            NetworkPurpose.Update => redirect ?? RedirectPolicy.GitHub,
            NetworkPurpose.ModelDownload => redirect ?? RedirectPolicy.HuggingFace,
            _ => null,
        };
        HttpMessageHandler handler = new PolicyHandler(purpose, effective, sockets);
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? purpose switch
            {
                NetworkPurpose.Update => TimeSpan.FromSeconds(30),
                NetworkPurpose.Cleanup => TimeSpan.FromSeconds(15),
                NetworkPurpose.Transcription => TimeSpan.FromSeconds(60),
                _ => Timeout.InfiniteTimeSpan, // モデル取得は大きい。取消は CancellationToken で行う
            },
        };
    }

    /// <summary>スキーム規則と、検証つきリダイレクト追跡。各 hop は内側のハンドラーで経路が再計算される。</summary>
    private sealed class PolicyHandler(NetworkPurpose purpose, RedirectPolicy? redirect, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CheckScheme(request.RequestUri);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (redirect is null) return response;

            var hops = 0;
            while (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null) return response;
                if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                {
                    response.Dispose();
                    throw new NetworkPolicyException("GET/HEAD 以外のリダイレクトは追いません");
                }
                if (++hops > redirect.MaxHops)
                {
                    response.Dispose();
                    throw new NetworkPolicyException($"リダイレクトが {redirect.MaxHops} 回を超えました");
                }
                var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                response.Dispose();
                if (next.Scheme != Uri.UriSchemeHttps) throw new NetworkPolicyException("HTTPS 以外へのリダイレクトは追いません");
                if (!redirect.IsHostAllowed(next)) throw new NetworkPolicyException($"許可されていないホストへのリダイレクト: {next.Host}");

                var follow = new HttpRequestMessage(request.Method, next);
                foreach (var h in request.Headers)
                {
                    // ホストが変わったら認証情報を渡さない
                    if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !SameOrigin(request.RequestUri!, next)) continue;
                    follow.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                request = follow;
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            return response;
        }

        private void CheckScheme(Uri? uri)
        {
            if (uri is null || !uri.IsAbsoluteUri) throw new NetworkPolicyException("絶対 URL が必要です");
            if (uri.Scheme == Uri.UriSchemeHttps) return;
            if (uri.Scheme == Uri.UriSchemeHttp && AllowsPlainHttp(uri)) return;
            throw new NetworkPolicyException("HTTPS 以外への通信は許可されていません（整文・文字起こしのローカルホスト HTTP を除く）");
        }

        /// <summary>HTTP を許す用途の明示列挙。Cleanup は従来の loopback 判定のまま、Transcription は URL 検証と同じ正確な3形だけ。</summary>
        private bool AllowsPlainHttp(Uri uri) => purpose switch
        {
            NetworkPurpose.Cleanup => RoutingProxy.IsLoopback(uri),
            NetworkPurpose.Transcription => CompatibleEndpointUrl.IsExactLoopback(uri),
            _ => false,
        };

        private static bool IsRedirect(HttpStatusCode c) => c is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        private static bool SameOrigin(Uri a, Uri b) =>
            string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && a.Scheme == b.Scheme;
    }
}
