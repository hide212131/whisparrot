namespace Whisparrot.Infrastructure;

/// <summary>
/// 配置パスの基点。設計 §2.2: 基点は <see cref="Environment.ProcessPath"/>（exe のあるフォルダ）。
/// カレントディレクトリや <c>AppContext.BaseDirectory</c>（単一ファイル展開先を指し得る）は使わない。
/// </summary>
public static class AppPaths
{
    public static string AppDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException("ProcessPath が取得できません");

    public static string ModelsDirectory => Path.Combine(AppDirectory, "models");

    /// <summary>M1 の暫定既定。モデル選択の設定化は T07 / T09。</summary>
    public static string DefaultModelPath => Path.Combine(ModelsDirectory, "ggml-small.bin");

    /// <summary>再生成可能な実行キャッシュ（native DLL の展開先）。設計 §2.2。</summary>
    public static string RuntimeCacheRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisparrot", "runtime");
}
