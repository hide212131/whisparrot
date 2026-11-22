namespace Whisparrot.Core.FileInput;

/// <summary><c>whisparrot transcribe &lt;入力ファイル&gt; [--out &lt;出力フォルダ&gt;] [--no-polish]</c> の引数。</summary>
public sealed record TranscribeOptions(string InputPath, string? OutDirectory, bool NoPolish)
{
    public const string Usage = "使い方: whisparrot transcribe <入力ファイル> [--out <出力フォルダ>] [--no-polish]";

    /// <summary><paramref name="args"/> は "transcribe" を含まない残り。不正なら null と理由（利用者向け）を返す。</summary>
    public static TranscribeOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? input = null, outDir = null;
        var noPolish = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--no-polish":
                    noPolish = true;
                    break;
                case "--out":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "--out には出力フォルダが必要です";
                        return null;
                    }
                    outDir = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"不明なオプションです: {a}";
                        return null;
                    }
                    if (input is not null)
                    {
                        error = "入力ファイルは一つだけ指定できます";
                        return null;
                    }
                    input = a;
                    break;
            }
        }
        if (input is null)
        {
            error = "入力ファイルが必要です";
            return null;
        }
        error = null;
        return new TranscribeOptions(input, outDir, noPolish);
    }
}
