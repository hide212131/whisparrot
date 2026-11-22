using System.Text;
using System.Text.Json;
using Whisparrot.Core.Settings;

namespace Whisparrot.Core.FileInput;

/// <summary>実行条件の記録。モデル名は設定から読む（秘密・URL は含めない）。</summary>
public sealed record TranscribeRecord(
    string AppVersion,
    DateTimeOffset StartedAt,
    string InputFile,
    string InputFormat,
    TimeSpan? AudioDuration,
    TimeSpan? DecodeTime,
    AppSettings? Settings,
    bool PolishRequested,
    TimeSpan TotalTime);

public static class TranscribeOutput
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>出力ファイルの基底名（入力ファイル名から拡張子を除いたもの）。</summary>
    public static string StemOf(string inputPath) => Path.GetFileNameWithoutExtension(inputPath);

    public static double Ms(TimeSpan? t) => t is { } v ? Math.Round(v.TotalMilliseconds) : 0;

    public static string BuildResultJson(TranscribeRecord rec, TranscribeRun run)
    {
        var s = rec.Settings;
        string? recModel = null, polishProvider = null, polishModel = null;
        if (s is not null)
        {
            recModel = run.Engine == RecognitionEngine.Cloud ? s.Recognition.Cloud.Model : s.Recognition.Model;
            (polishProvider, polishModel) = s.Cleanup.Provider switch
            {
                CleanupProvider.Compatible => ("compatible", s.Cleanup.Compatible.Model),
                CleanupProvider.ChatGpt => ("chatgpt", s.Cleanup.ChatGpt.Model),
                _ => ("off", (string?)null),
            };
        }
        object? Time(TimeSpan? t) => t is null ? null : Ms(t);

        var doc = new Dictionary<string, object?>
        {
            ["app_version"] = rec.AppVersion,
            ["started_at"] = rec.StartedAt.ToString("o"),
            ["success"] = run.Status == TranscribeStatus.Ok,
            ["error"] = run.Error,
            ["input"] = new Dictionary<string, object?>
            {
                ["file"] = rec.InputFile,
                ["format"] = rec.InputFormat,
                ["duration_seconds"] = rec.AudioDuration is { } d ? Math.Round(d.TotalSeconds, 3) : null,
            },
            ["recognition"] = new Dictionary<string, object?>
            {
                ["engine"] = run.Engine?.ToString().ToLowerInvariant(),
                ["model"] = recModel,
                ["backend"] = run.Backend,
                ["language"] = s?.Recognition.Language,
                ["ok"] = run.RawText is not null,
            },
            ["polish"] = new Dictionary<string, object?>
            {
                ["requested"] = rec.PolishRequested,
                ["provider"] = polishProvider,
                ["model"] = polishModel,
                ["result"] = run.Polish.ToString().ToLowerInvariant(),
            },
            ["timings_ms"] = new Dictionary<string, object?>
            {
                ["decode"] = Time(rec.DecodeTime),
                ["recognize"] = Time(run.RecognizeTime),
                ["polish"] = Time(run.PolishTime),
                ["total"] = Ms(rec.TotalTime),
            },
        };
        return JsonSerializer.Serialize(doc, Indented) + "\n";
    }

    /// <summary>
    /// 出力フォルダ（無ければ作る）へ書く。原文が無ければ txt は作らない。--no-polish や整文失敗では .polished.txt を作らない。
    /// 失敗した実行でも .result.json は残す。書いたファイルのパスを返す。
    /// </summary>
    public static IReadOnlyList<string> Write(string directory, string stem, TranscribeRecord rec, TranscribeRun run)
    {
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        var written = new List<string>();
        void Put(string suffix, string text)
        {
            var path = Path.Combine(directory, stem + suffix);
            File.WriteAllText(path, text, utf8);
            written.Add(path);
        }
        if (run.RawText is not null) Put(".raw.txt", run.RawText + "\n");
        if (run.PolishText is not null) Put(".polished.txt", run.PolishText + "\n");
        Put(".result.json", BuildResultJson(rec, run));
        return written;
    }
}
