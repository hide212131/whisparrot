using System.Text.Json;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.ChatGpt;

/// <summary>保存する資格。クライアント ID は OpenAI がログインごとに発行したもので、このインストールだけの資格になる。</summary>
public sealed record ChatGptTokens(string ClientId, string InstallationId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc, string Scope)
{
    public bool HasRequiredScope => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ChatGptEndpoints.RequiredScope);

    /// <summary>ログ・診断に出さない（秘密を含む既定の record 表示を潰す）。</summary>
    public override string ToString() => "ChatGptTokens(redacted)";
}

public enum TokenFileState { Missing, Ok, Unreadable }

public sealed record TokenLoad(TokenFileState State, ChatGptTokens? Tokens, string? Problem = null);

/// <summary>
/// 設計 §8: config.json とは別の保存域（<c>chatgpt-token.json</c>）。access / refresh token は <see cref="ISecretProtector"/>（DPAPI）で
/// 暗号化して書き、クライアント ID・インストール UUID・期限・scope は平文。壊れた／別ユーザーの暗号文のファイルは消さず、Unreadable として報告する。
/// ログアウト = ファイルの削除。
/// </summary>
public sealed class ChatGptTokenStore(string path, ISecretProtector protector)
{
    private const int SchemaVersion = 1;
    private readonly object _lock = new();

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public TokenLoad Load()
    {
        lock (_lock)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path); }
            catch (FileNotFoundException) { return new TokenLoad(TokenFileState.Missing, null); }
            catch (DirectoryNotFoundException) { return new TokenLoad(TokenFileState.Missing, null); }
            catch (IOException) { return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルを読めません"); }
            catch (UnauthorizedAccessException) { return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルを読めません"); }

            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || Str(r, "schema_version", number: true) != SchemaVersion.ToString())
                    return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルの形式が未対応です");
                var clientId = Str(r, "client_id");
                var installId = Str(r, "installation_id");
                var scope = Str(r, "scope");
                var expires = Str(r, "expires_at_utc");
                if (clientId is null || installId is null || scope is null || expires is null
                    || !DateTimeOffset.TryParse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
                    return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルの内容が不正です");
                if (!protector.TryUnprotect(Str(r, "access_token_dpapi"), out var access) || !protector.TryUnprotect(Str(r, "refresh_token_dpapi"), out var refresh))
                    return new TokenLoad(TokenFileState.Unreadable, null, "token を復号できません（別のユーザーで保存された可能性）");
                if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                    return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルの内容が不正です");
                return new TokenLoad(TokenFileState.Ok, new ChatGptTokens(clientId, installId, access, refresh, expiresAt.ToUniversalTime(), scope));
            }
            catch (JsonException)
            {
                return new TokenLoad(TokenFileState.Unreadable, null, "token ファイルが壊れています");
            }
        }
    }

    /// <summary>同階層の一時ファイルへ書いて置換する（途中で落ちても旧ファイルが残る）。</summary>
    public void Save(ChatGptTokens t)
    {
        lock (_lock)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema_version", SchemaVersion);
                w.WriteString("installation_id", t.InstallationId);
                w.WriteString("client_id", t.ClientId);
                w.WriteString("access_token_dpapi", protector.Protect(t.AccessToken));
                w.WriteString("refresh_token_dpapi", protector.Protect(t.RefreshToken));
                w.WriteString("expires_at_utc", t.ExpiresAtUtc.UtcDateTime.ToString("O"));
                w.WriteString("scope", t.Scope);
                w.WriteEndObject();
            }
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(ms.GetBuffer(), 0, (int)ms.Length);
                fs.Flush(true);
            }
            File.Move(tmp, Path, overwrite: true);
        }
    }

    /// <summary>ログアウト: ファイルを削除する。無ければ何もしない。</summary>
    public void Delete()
    {
        lock (_lock)
        {
            File.Delete(Path);
            File.Delete(Path + ".tmp");
        }
    }

    private static string? Str(JsonElement obj, string name, bool number = false)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number when number => v.GetRawText(),
            _ => null,
        };
    }
}
