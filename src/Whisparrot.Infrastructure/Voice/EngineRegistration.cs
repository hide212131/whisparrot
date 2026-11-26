using System.Text;
using System.Text.Json.Nodes;

namespace Whisparrot.Infrastructure.Voice;

/// <summary>
/// 常駐アプリの自己登録（設計 §5 P3 / 決定: 検出利用）。Hane が既知の位置から exe の絶対パスと版を読み、子プロセスとして起動できるようにする。
/// 場所は <c>%LOCALAPPDATA%\whisparrot\engine-registration.json</c>。常駐アプリの起動ごとに現在の exe・版で上書きする（自己更新後も新しい値になる）。
/// エンジンモードからは呼ばない。書けなくても起動は止めない。
/// </summary>
public static class EngineRegistration
{
    public const string FileName = "engine-registration.json";

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisparrot", FileName);

    /// <summary>登録を書く。失敗しても例外は出さず false を返す（登録は利便であり、アプリの起動条件ではない）。</summary>
    public static bool TryWrite(string exePath, string version, DateTimeOffset now, string? path = null)
    {
        try
        {
            Write(path ?? DefaultPath, exePath, version, now);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>一時ファイルへ書いて Flush(true) 後に置換する（既存なら File.Replace、新規なら Move）。</summary>
    public static void Write(string path, string exePath, string version, DateTimeOffset now)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        var json = new JsonObject
        {
            ["exePath"] = Path.GetFullPath(exePath),
            ["version"] = version,
            ["engine"] = true, // voice/1 対応
            ["updatedAt"] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };

        var tmp = Path.Combine(dir, FileName + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(json.ToJsonString());
                fs.Write(bytes);
                fs.Flush(true);
            }
            if (File.Exists(full)) File.Replace(tmp, full, null);
            else File.Move(tmp, full);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
