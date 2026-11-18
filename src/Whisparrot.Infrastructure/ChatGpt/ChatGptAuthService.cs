using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Network;

namespace Whisparrot.Infrastructure.ChatGpt;

public enum ChatGptLoginFailure { PortInUse, Cancelled, TimedOut, Denied, InvalidCallback, Network, Rejected, ScopeMissing, Storage }

/// <summary>ログイン失敗。メッセージは利用者向けで、token・コード・応答本文は含めない。</summary>
public sealed class ChatGptLoginException(ChatGptLoginFailure kind, string message) : Exception(message)
{
    public ChatGptLoginFailure Kind { get; } = kind;
}

public enum ChatGptConnection { LoggedOut, LoggedIn, NeedsRelogin }

/// <summary>設定画面向けの接続状態。<paramref name="Detail"/> に秘密は入らない。</summary>
public sealed record ChatGptStatus(ChatGptConnection State, DateTimeOffset? AccessExpiresAtUtc = null, string? Detail = null);

/// <summary>整文用の access token の取得結果。失敗は <see cref="PolishFailure"/> に分類済みで、例外にしない。</summary>
public sealed record AccessTokenResult(string? Token, PolishFailure Failure = PolishFailure.Auth, string? Detail = null)
{
    public static AccessTokenResult Ok(string token) => new(token);
    public static AccessTokenResult Fail(PolishFailure f, string detail) => new(null, f, detail);
}

/// <summary>
/// 設計 §7: 公式 Sign in with ChatGPT の直接 OAuth（認可コード + PKCE、動的クライアント ID）。
/// 通信は <see cref="NetworkContext"/>（Cleanup）だけを通す。ブラウザの通信自体はアプリのプロキシ制御の外（設計 U-02）。
/// token は <see cref="ChatGptTokenStore"/> にだけ置き、ログ・例外・診断に出さない。
/// </summary>
public sealed class ChatGptAuthService
{
    /// <summary>期限のこの時間前から更新する。</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly NetworkContext _network;
    private readonly ChatGptTokenStore _store;
    private readonly ChatGptEndpoints _endpoints;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _reloginRequired;

    public ChatGptAuthService(NetworkContext network, ChatGptTokenStore store, ChatGptEndpoints? endpoints = null, Func<DateTimeOffset>? clock = null)
    {
        _network = network;
        _store = store;
        _endpoints = endpoints ?? ChatGptEndpoints.Official;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ChatGptEndpoints Endpoints => _endpoints;

    // ---- 状態 ----

    public ChatGptStatus GetStatus()
    {
        var load = _store.Load();
        switch (load.State)
        {
            case TokenFileState.Missing: return new ChatGptStatus(ChatGptConnection.LoggedOut);
            case TokenFileState.Unreadable: return new ChatGptStatus(ChatGptConnection.NeedsRelogin, null, load.Problem);
        }
        var t = load.Tokens!;
        if (!t.HasRequiredScope) return new ChatGptStatus(ChatGptConnection.NeedsRelogin, t.ExpiresAtUtc, "必要な権限（scope）がありません");
        if (_reloginRequired) return new ChatGptStatus(ChatGptConnection.NeedsRelogin, t.ExpiresAtUtc, "token の更新に失敗しました");
        return new ChatGptStatus(ChatGptConnection.LoggedIn, t.ExpiresAtUtc);
    }

    /// <summary>整文の準備時の同期チェック。使えない理由（利用者向け）を返す。使えそうなら null。ネットワークは使わない。</summary>
    public string? CheckReady()
    {
        var s = GetStatus();
        return s.State switch
        {
            ChatGptConnection.LoggedIn => null,
            ChatGptConnection.LoggedOut => "ChatGPT にログインしていません。設定でログインしてください",
            _ => $"ChatGPT のログインが無効です（{s.Detail}）。設定でログインし直してください",
        };
    }

    // ---- ログアウト ----

    public void Logout()
    {
        _gate.Wait();
        try { _store.Delete(); _reloginRequired = false; }
        finally { _gate.Release(); }
    }

    // ---- ログイン ----

    /// <summary>
    /// ログインを始める。コールバック用のループバック待受を先に張り（使用中なら <see cref="ChatGptLoginFailure.PortInUse"/>）、
    /// ブラウザで開く認可 URL を返す。ブラウザを開くのは呼び出し側（利用者の明示操作の後だけ）。
    /// </summary>
    public ChatGptLoginAttempt BeginLogin()
    {
        var listener = new TcpListener(IPAddress.Loopback, _endpoints.RedirectPort) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new ChatGptLoginException(ChatGptLoginFailure.PortInUse,
                $"ポート {_endpoints.RedirectPort} が使用中です。別のログインが進行中の可能性があります。完了または取消してからやり直してください");
        }
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var installationId = _store.Load().Tokens?.InstallationId ?? Guid.NewGuid().ToString();
        return new ChatGptLoginAttempt(this, listener, port, installationId);
    }

    public sealed class ChatGptLoginAttempt : IDisposable
    {
        private readonly ChatGptAuthService _svc;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<CallbackParams> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        private readonly string _state = Base64Url(RandomNumberGenerator.GetBytes(16));
        private readonly string _installationId;
        private readonly string _redirectUri;

        internal ChatGptLoginAttempt(ChatGptAuthService svc, TcpListener listener, int port, string installationId)
        {
            _svc = svc;
            _listener = listener;
            _installationId = installationId;
            _redirectUri = $"http://127.0.0.1:{port}/auth/callback";
            AuthorizeUrl = svc.BuildAuthorizeUrl(_redirectUri, _state, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier))), Base64Url(RandomNumberGenerator.GetBytes(16)), installationId);
            _ = Task.Run(AcceptLoop);
            Completion = Task.Run(RunAsync);
        }

        public Uri AuthorizeUrl { get; }

        /// <summary>完了（成功なら接続状態、失敗は <see cref="ChatGptLoginException"/>）。</summary>
        public Task<ChatGptStatus> Completion { get; }

        /// <summary>ブラウザのリダイレクト先 URL（または <c>code=...&amp;state=...</c> のクエリ）の貼り付け。不正なら例外で、ログインは続行できる。</summary>
        public void SubmitPasted(string text)
        {
            var p = ParsePasted(text) ?? throw new ChatGptLoginException(ChatGptLoginFailure.InvalidCallback, "貼り付けた内容からログイン結果を読み取れません。リダイレクト後の URL 全体を貼り付けてください");
            if (!StateMatches(p.State))
                throw new ChatGptLoginException(ChatGptLoginFailure.InvalidCallback, "貼り付けた内容は、この画面で開始したログインのものではありません");
            _callback.TrySetResult(p);
        }

        public void Cancel() => _callback.TrySetException(new ChatGptLoginException(ChatGptLoginFailure.Cancelled, "ログインを取り消しました"));

        public void Dispose()
        {
            Cancel();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _listener.Stop();
        }

        private bool StateMatches(string? s) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s ?? ""), Encoding.UTF8.GetBytes(_state));

        private async Task<ChatGptStatus> RunAsync()
        {
            try
            {
                CallbackParams p;
                using (var timeout = new CancellationTokenSource(LoginTimeout))
                using (timeout.Token.Register(() => _callback.TrySetException(new ChatGptLoginException(ChatGptLoginFailure.TimedOut, "ログインがタイムアウトしました。もう一度お試しください"))))
                    p = await _callback.Task.ConfigureAwait(false);
                // 待受はここで閉じる（以後のポート占有を残さない）。
                _listener.Stop();
                if (p.Error is not null)
                    throw new ChatGptLoginException(ChatGptLoginFailure.Denied, "サインインが完了しませんでした（認可画面で拒否またはエラー）");
                return await _svc.CompleteAsync(p, _verifier, _redirectUri, _installationId).ConfigureAwait(false);
            }
            finally
            {
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                _listener.Stop();
            }
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    using var io = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    io.CancelAfter(TimeSpan.FromSeconds(5));
                    var stream = tcp.GetStream();
                    var head = await ReadHeadAsync(stream, io.Token).ConfigureAwait(false);
                    var line = head?.Split("\r\n", 2)[0].Split(' ');
                    if (line is not { Length: 3 } || line[0] != "GET") { await RespondAsync(stream, 405, "Method Not Allowed", io.Token).ConfigureAwait(false); return; }
                    var target = line[1];
                    var q = target.IndexOf('?');
                    var path = q < 0 ? target : target[..q];
                    if (path != "/auth/callback") { await RespondAsync(stream, 404, "Not Found", io.Token).ConfigureAwait(false); return; }

                    var p = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
                    // 他ソフトの先回りリクエストでログインを壊されないよう、state が合うものだけ受け付ける。
                    if (!StateMatches(p.State))
                    { await RespondAsync(stream, 400, "ログイン要求が一致しません。設定画面からやり直してください。", io.Token).ConfigureAwait(false); return; }
                    var ok = p.Error is null && !string.IsNullOrEmpty(p.Code);
                    await RespondAsync(stream, 200, ok ? "ログインを受け付けました。このタブを閉じて whisparrot に戻ってください。" : "サインインが完了しませんでした。whisparrot に戻ってください。", io.Token).ConfigureAwait(false);
                    _callback.TrySetResult(p);
                }
                catch { /* 切断・時間切れ。無視 */ }
            }
        }

        private static async Task<string?> ReadHeadAsync(Stream s, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await s.ReadAsync(buf, ct).ConfigureAwait(false) == 0) return null;
                sb.Append((char)buf[0]);
                if (sb.Length > 16 * 1024) return null;
            }
            return sb.ToString();
        }

        private static async Task RespondAsync(Stream s, int status, string message, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>whisparrot</title><p>{WebUtility.HtmlEncode(message)}</p>");
            var head = $"HTTP/1.1 {status} X\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await s.WriteAsync(body, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    internal sealed record CallbackParams(string? Code, string? State, string? ClientId, string? Error);

    internal static CallbackParams ParseQuery(string query)
    {
        string? code = null, state = null, clientId = null, error = null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            switch (k)
            {
                case "code": code = v; break;
                case "state": state = v; break;
                case "client_id": clientId = v; break;
                case "error": error = v; break;
            }
        }
        return new CallbackParams(code, state, clientId, error);
    }

    /// <summary>貼り付け: 完全な URL・パス付きの断片・クエリ文字列のいずれも受ける。</summary>
    internal static CallbackParams? ParsePasted(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 8192) return null;
        string query;
        if (Uri.TryCreate(text, UriKind.Absolute, out var u)) query = u.Query;
        else
        {
            var q = text.IndexOf('?');
            query = q >= 0 ? text[(q + 1)..] : text;
        }
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        var p = ParseQuery(query);
        return p.State is null || (p.Code is null && p.Error is null) ? null : p;
    }

    internal Uri BuildAuthorizeUrl(string redirectUri, string state, string challenge, string nonce, string installationId)
    {
        var args = new (string, string)[]
        {
            ("client_id", ChatGptEndpoints.DynamicClientId),
            ("agent_name_hint", ChatGptEndpoints.AgentName),
            ("ext_agent_host_id", "urn:uuid:" + installationId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("resource", _endpoints.Resource),
            ("scope", ChatGptEndpoints.Scope),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("nonce", nonce),
        };
        var q = string.Join("&", args.Select(a => $"{a.Item1}={Uri.EscapeDataString(a.Item2)}"));
        return new Uri(_endpoints.AuthorizeUrl.GetLeftPart(UriPartial.Path) + "?" + q);
    }

    // ---- token 交換・更新 ----

    private async Task<ChatGptStatus> CompleteAsync(CallbackParams p, string verifier, string redirectUri, string installationId)
    {
        if (string.IsNullOrEmpty(p.Code))
            throw new ChatGptLoginException(ChatGptLoginFailure.InvalidCallback, "ログイン結果に認可コードがありません");
        if (!IsPlausibleClientId(p.ClientId))
            throw new ChatGptLoginException(ChatGptLoginFailure.InvalidCallback, "ログイン結果に発行されたクライアント ID がありません");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = p.ClientId!,
            ["code"] = p.Code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = _endpoints.Resource,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var r = await PostTokenAsync(form, deadline.Token).ConfigureAwait(false);
        if (r.Failure is { } f)
            throw new ChatGptLoginException(f == PolishFailure.Auth ? ChatGptLoginFailure.Rejected : ChatGptLoginFailure.Network,
                f == PolishFailure.Auth ? "ChatGPT がログインを受け付けませんでした" : "ChatGPT に接続できませんでした");
        var tok = r.Parsed!;
        if (tok.RefreshToken is null || tok.Scope is null)
            throw new ChatGptLoginException(ChatGptLoginFailure.Rejected, "ChatGPT の応答が想定と異なります");
        var tokens = new ChatGptTokens(p.ClientId!, installationId, tok.AccessToken, tok.RefreshToken, _clock().AddSeconds(tok.ExpiresIn), tok.Scope);
        if (!tokens.HasRequiredScope)
            throw new ChatGptLoginException(ChatGptLoginFailure.ScopeMissing, "このアカウントでは ChatGPT の利用権限（chatgpt.tokens.use.direct）が付与されませんでした");

        await _gate.WaitAsync().ConfigureAwait(false);
        try { _store.Save(tokens); _reloginRequired = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ChatGptLoginException(ChatGptLoginFailure.Storage, "ログイン情報を保存できませんでした");
        }
        finally { _gate.Release(); }
        return GetStatus();
    }

    /// <summary>整文用の access token。期限の <see cref="RefreshMargin"/> 前なら更新してから返す。<paramref name="ct"/> の取消だけが例外。</summary>
    public async Task<AccessTokenResult> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var load = _store.Load();
            if (load.State != TokenFileState.Ok) return AccessTokenResult.Fail(PolishFailure.Auth, "未ログイン");
            var t = load.Tokens!;
            if (!t.HasRequiredScope) return AccessTokenResult.Fail(PolishFailure.Auth, "scope 不足");
            if (t.ExpiresAtUtc - _clock() > RefreshMargin) return AccessTokenResult.Ok(t.AccessToken);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = t.ClientId,
                ["refresh_token"] = t.RefreshToken,
                ["resource"] = _endpoints.Resource,
            };
            var r = await PostTokenAsync(form, ct).ConfigureAwait(false);
            if (r.Failure is { } f)
            {
                if (f == PolishFailure.Auth) _reloginRequired = true;
                return AccessTokenResult.Fail(f, "token 更新に失敗");
            }
            var tok = r.Parsed!;
            var next = t with
            {
                AccessToken = tok.AccessToken,
                RefreshToken = tok.RefreshToken ?? t.RefreshToken, // ローテーションしない応答なら旧値を保つ
                ExpiresAtUtc = _clock().AddSeconds(tok.ExpiresIn),
                Scope = tok.Scope ?? t.Scope,
            };
            if (!next.HasRequiredScope) { _reloginRequired = true; return AccessTokenResult.Fail(PolishFailure.Auth, "scope 不足"); }
            try { _store.Save(next); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // 保存できなくても、今回の整文は新しい token で続けられる。
            }
            _reloginRequired = false;
            return AccessTokenResult.Ok(next.AccessToken);
        }
        finally { _gate.Release(); }
    }

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, long ExpiresIn, string? Scope);

    private sealed record TokenPost(TokenResponse? Parsed, PolishFailure? Failure);

    /// <summary>token エンドポイントへの POST。失敗は分類（Auth=資格を拒否 / Network / Http=サーバー側 / Malformed）だけを返し、本文は出さない。</summary>
    private async Task<TokenPost> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var client = _network.CreateClient(NetworkPurpose.Cleanup, timeout: Timeout.InfiniteTimeSpan);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoints.TokenUrl) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 400 or 401 or 403) return new TokenPost(null, PolishFailure.Auth);
            if (status is < 200 or > 299) return new TokenPost(null, PolishFailure.Http);
            var body = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return body is null ? new TokenPost(null, PolishFailure.TooLarge) : ParseToken(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TokenPost(null, PolishFailure.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or NetworkConfigurationException or NetworkPolicyException)
        {
            return new TokenPost(null, PolishFailure.Network);
        }
    }

    private static TokenPost ParseToken(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(at.GetString()))
                return new TokenPost(null, PolishFailure.Malformed);
            string? refresh = r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String && rt.GetString() is { Length: > 0 } rs ? rs : null;
            var expires = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number && ei.TryGetInt64(out var e) && e > 0 ? e : 3600;
            string? scope = r.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
            return new TokenPost(new TokenResponse(at.GetString()!, refresh, expires, scope), null);
        }
        catch (JsonException)
        {
            return new TokenPost(null, PolishFailure.Malformed);
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        await using var _ = stream.ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > 64 * 1024) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static bool IsPlausibleClientId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 256 && id.All(c => c > ' ' && c < 127);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
