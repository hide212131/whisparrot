using System.Net;
using Whisparrot.Core.Settings;

namespace Whisparrot.Infrastructure.Network;

/// <summary>プロキシ設定の不備。黙って直結へ逃げず、通信エラーとして呼び出し側へ返す（設計 §9.1）。</summary>
public sealed class NetworkConfigurationException(string message) : Exception(message);

/// <summary>ポリシー違反（HTTPS 以外、許可外ホストへのリダイレクト、ホップ超過など）。</summary>
public sealed class NetworkPolicyException(string message) : Exception(message);

/// <summary>正規化したプロキシの指定（認証情報は URL から分離済み）。</summary>
public sealed record ProxyTarget(Uri Uri, NetworkCredential? Credential)
{
    /// <summary>表示用（認証情報を含めない）。</summary>
    public string HostPort => $"{Uri.Host}:{Uri.Port}";

    /// <summary>
    /// <c>host:port</c> / <c>http://[user[:pass]@]host[:port]</c> を正規化する。https:// や socks 等は受け付けない。
    /// </summary>
    public static ProxyTarget Parse(string raw, string source)
    {
        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new NetworkConfigurationException($"{source}: プロキシ URL に空白・制御文字が含まれています");
        if (ProxyUrlRules.Validate(StripUserInfo(text), allowUserInfo: true) is { } err)
            throw new NetworkConfigurationException($"{source}: {err}");
        var uri = new Uri(text);
        NetworkCredential? cred = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            cred = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return new ProxyTarget(new Uri($"http://{uri.Host}:{uri.Port}"), cred);
    }

    private static string StripUserInfo(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOfAny(['/', '?', '#'], i);
        var authority = end < 0 ? url[i..] : url[i..end];
        var at = authority.LastIndexOf('@');
        return at < 0 ? url : url[..i] + url[(i + at + 1)..];
    }
}

/// <summary>起動時環境の <c>HTTPS_PROXY</c> / <c>https_proxy</c> / <c>NO_PROXY</c> / <c>no_proxy</c> を正規化したスナップショット。</summary>
public sealed record ProxyEnvironment(string? HttpsProxy, string? NoProxy, IReadOnlyList<string> Conflicts)
{
    public static ProxyEnvironment Empty { get; } = new(null, null, []);

    public static ProxyEnvironment Capture() => From(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Select(e => KeyValuePair.Create((string)e.Key, e.Value as string)));

    /// <summary>
    /// 大文字小文字を無視して拾い、空文字は未指定とする。複数あれば完全一致の大文字キーを優先し、
    /// 異なる値が併存する場合は <see cref="Conflicts"/> に記録する（設定画面で知らせる）。
    /// </summary>
    public static ProxyEnvironment From(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        var all = variables.ToList();
        var conflicts = new List<string>();
        string? Pick(string upper)
        {
            var hits = all.Where(kv => string.Equals(kv.Key, upper, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => (kv.Key, Value: kv.Value!.Trim())).ToList();
            if (hits.Count == 0) return null;
            if (hits.Select(h => h.Value).Distinct().Count() > 1) conflicts.Add($"{upper} と {string.Join("/", hits.Select(h => h.Key).Where(k => k != upper))} の値が異なります（{upper} を優先）");
            var exact = hits.FirstOrDefault(h => h.Key == upper);
            return exact.Key is not null ? exact.Value : hits[0].Value;
        }
        return new ProxyEnvironment(Pick("HTTPS_PROXY"), Pick("NO_PROXY"), conflicts);
    }
}

/// <summary>
/// <c>NO_PROXY</c>: カンマ区切り。<c>*</c>、ホストの完全一致、先頭ドット付きドメイン（そのドメイン自身とサブドメイン）、
/// IP リテラル（IPv6 はブラケット）、任意の <c>:port</c> に対応する。CIDR・ワイルドカード・正規表現は対応せず設定エラーにする。
/// ドメイン境界を確認する（<c>.example.com</c> は <c>notexample.com</c> に一致しない）。
/// </summary>
public sealed class NoProxyList
{
    private readonly bool _all;
    private readonly List<Entry> _entries = [];

    private sealed record Entry(string Host, IPAddress? Ip, bool DomainSuffix, int? Port);

    public static NoProxyList Empty { get; } = new(null);

    public NoProxyList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var part in raw.Split(','))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            if (item == "*") { _all = true; continue; }
            _entries.Add(ParseEntry(item));
        }
    }

    private static Entry ParseEntry(string item)
    {
        string host = item;
        int? port = null;
        if (item.StartsWith('['))
        {
            var close = item.IndexOf(']');
            if (close < 0) throw Bad(item);
            host = item[1..close];
            var rest = item[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryPort(rest[1..], out var p)) throw Bad(item);
                port = p;
            }
            if (!IPAddress.TryParse(host, out var ip6) || ip6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) throw Bad(item);
            return new Entry(host, ip6, false, port);
        }

        if (item.Count(c => c == ':') == 1)
        {
            var i = item.IndexOf(':');
            if (!TryPort(item[(i + 1)..], out var p)) throw Bad(item);
            host = item[..i];
            port = p;
        }
        else if (item.Contains(':')) throw Bad(item); // ブラケットなしの IPv6 は曖昧

        if (host.Length == 0 || host.Contains('/') || host.Contains('*') || host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':')) || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || host.Contains(".."))
            throw Bad(item);
        var suffix = host.StartsWith('.');
        if (suffix) host = host[1..];
        if (host.Length == 0) throw Bad(item);
        if (IPAddress.TryParse(host, out var ip))
        {
            if (suffix) throw Bad(item);
            return new Entry(host, ip, false, port);
        }
        return new Entry(host.ToLowerInvariant().TrimEnd('.'), null, suffix, port);
    }

    private static bool TryPort(string s, out int port) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, null, out port) && port is >= 1 and <= 65535;

    private static NetworkConfigurationException Bad(string item) =>
        new($"NO_PROXY の指定に対応していません: {item}（対応: *, ホスト, .ドメイン, IP, [IPv6], :port。CIDR・ワイルドカード・正規表現は不可）");

    public bool Matches(Uri dest)
    {
        if (_all) return true;
        var host = dest.Host.Trim('[', ']').ToLowerInvariant().TrimEnd('.');
        IPAddress.TryParse(host, out var destIp);
        foreach (var e in _entries)
        {
            if (e.Port is { } p && p != dest.Port) continue;
            if (e.Ip is not null)
            {
                if (destIp is not null && e.Ip.Equals(destIp)) return true;
                continue;
            }
            if (host == e.Host) return true;
            if (e.DomainSuffix && host.EndsWith("." + e.Host, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>経路規則を一か所にまとめた <see cref="IWebProxy"/>。loopback は常に直結。</summary>
public sealed class RoutingProxy(ProxyTarget? proxy, NoProxyList noProxy) : IWebProxy
{
    public ICredentials? Credentials { get => proxy?.Credential; set { } }

    public static bool IsLoopback(Uri u) =>
        u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    public bool IsBypassed(Uri host) => proxy is null || IsLoopback(host) || noProxy.Matches(host);

    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : proxy!.Uri;
}
