using System.Security.Cryptography;
using System.Text;
using Whisparrot.Core.Settings;

namespace Whisparrot.Infrastructure.Settings;

/// <summary>読み込みが失敗した設定ファイルの上に保存しようとした。非破壊の原則で拒否する。</summary>
public sealed class SettingsBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// 設計 §8: <c>config.json</c> の読み書き。未来の schema・壊れた JSON・不正値は無断で初期化せず、ファイルに触れない。
/// 保存は同階層の一時ファイルへ書いて <c>Flush(true)</c> 後に置換する（既存なら <c>ReplaceFileW</c>、新規なら rename）。
/// </summary>
public sealed class JsonSettingsStore
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _readOnly;
    private SettingsLoadResult? _loaded;

    /// <param name="readOnly">読み取り専用で開く（エンジンモード）。読み込みで一時ファイルの掃除もしない。保存は呼ばない前提。</param>
    public JsonSettingsStore(string path, Func<DateTimeOffset>? clock = null, bool readOnly = false)
    {
        _readOnly = readOnly;
        Path = System.IO.Path.GetFullPath(path);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Path { get; }

    /// <summary>読み込み済みで、書き込める状態か（未読み込み・読み込み失敗なら false）。</summary>
    public bool CanSave => _loaded?.Usable == true;

    public SettingsLoadResult Load()
    {
        lock (_lock)
        {
            if (!_readOnly) CleanStaleTempFiles();
            return _loaded = Read();
        }
    }

    private SettingsLoadResult Read()
    {
        if (!File.Exists(Path))
            return new(SettingsLoadStatus.Missing, AppSettings.Default, []);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path));
        }
        catch (Exception ex) when (ex is DecoderFallbackException)
        {
            return new(SettingsLoadStatus.Corrupt, AppSettings.Default, [new SettingsIssue("$", "UTF-8 として読めません")]);
        }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return SettingsJson.Parse(text, _clock());
    }

    /// <summary>検証して保存する。検証エラー・読み込み失敗中の上書き・ディスク上の新しすぎる schema は拒否する。</summary>
    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            if (_loaded is null) throw new SettingsBlockedException("設定を読み込む前に保存はできません");
            if (!_loaded.Usable) throw new SettingsBlockedException("config.json を読み込めなかったため、上書き保存しません");

            var issues = SettingsValidator.Validate(settings, _clock());
            if (issues.Count > 0) throw new SettingsValidationException(issues);

            // 保存直前にディスク上の状態を再確認する（外部で新しい版が書いた場合に壊さない）。
            var disk = Read();
            if (!disk.Usable)
            {
                _loaded = disk;
                throw new SettingsBlockedException("保存直前の確認で config.json が読み込めない状態でした: " + disk.Describe());
            }

            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = SettingsJson.Serialize(settings);
                    fs.Write(bytes);
                    fs.Flush(true);
                }
                if (File.Exists(Path)) File.Replace(tmp, Path, null);
                else File.Move(tmp, Path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            _loaded = new SettingsLoadResult(SettingsLoadStatus.Loaded, settings, [], settings.SchemaVersion);
        }
    }

    /// <summary>
    /// 利用者の明示操作で、読めない config.json を <c>config.json.&lt;kind&gt;-&lt;UTC日時&gt;.bak</c> へ退避する（上書き・削除はしない）。
    /// 新しすぎる schema は退避できない（新しい版の設定を失わせないため）。
    /// </summary>
    public string BackupAndReset()
    {
        lock (_lock)
        {
            var current = _loaded ?? Read();
            if (current.Status == SettingsLoadStatus.UnsupportedSchema)
                throw new SettingsBlockedException("新しい版の設定は退避・初期化できません。新しい版の whisparrot を使ってください");
            if (current.Usable) throw new SettingsBlockedException("読めない状態ではないため、退避は不要です");
            var kind = current.Status == SettingsLoadStatus.Corrupt ? "corrupt" : "invalid";
            var backup = $"{Path}.{kind}-{_clock().UtcDateTime:yyyyMMddTHHmmssZ}.bak";
            File.Move(Path, backup);
            _loaded = new SettingsLoadResult(SettingsLoadStatus.Missing, AppSettings.Default, []);
            return backup;
        }
    }

    private void CleanStaleTempFiles()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, System.IO.Path.GetFileName(Path) + ".????????.tmp"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}

public sealed class SettingsValidationException(IReadOnlyList<SettingsIssue> issues)
    : Exception("設定が不正です: " + string.Join(" / ", issues))
{
    public IReadOnlyList<SettingsIssue> Issues { get; } = issues;
}
