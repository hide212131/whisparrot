namespace Whisparrot.Core.Settings;

public enum CleanupProvider { Off, Compatible, ChatGpt }

public enum ProxyMode { Environment, Manual, None }

/// <summary>手動プロキシの認証方式。<c>Windows</c> は OS のログオン資格情報（既定の資格情報）で NTLM / Negotiate に応答する。ユーザー名・パスワードとは排他。</summary>
public enum ProxyManualAuth { Credentials, Windows }

public enum InsertionMethod { Clipboard, SendInput }

public static class ModelIds
{
    public const string Base = "base";
    public const string Small = "small";
    public const string Medium = "medium";
    public const string LargeV3Turbo = "large-v3-turbo";

    public static IReadOnlyList<string> All { get; } = [Base, Small, Medium, LargeV3Turbo];
}

/// <summary>第1段の認識先（クラウド文字起こし設計 §4）。既定はローカル。排他選択で、並列・自動復帰はしない。</summary>
public enum RecognitionEngine { Local, Cloud }

/// <summary>OpenAI 互換の音声文字起こし API への接続設定。整文の互換接続とは独立に保存し、共有しない。秘密は暗号文（Base64）だけを持つ。</summary>
public sealed record CloudRecognitionSettings(string BaseUrl, string? ApiKeyDpapi, string Model, string Language = CloudRecognitionSettings.DefaultLanguage)
{
    /// <summary>言語コードの既定。空文字列は「指定しない」（language フィールドを送らず、接続先の自動判定に任せる）。</summary>
    public const string DefaultLanguage = "ja";

    public static CloudRecognitionSettings Empty { get; } = new("", null, "");

    /// <summary>全項目が既定（未設定）。設定ファイルには書き出さない。</summary>
    public bool IsEmpty => BaseUrl.Length == 0 && ApiKeyDpapi is null && Model.Length == 0 && Language == DefaultLanguage;
}

/// <summary><c>Model</c> / <c>Language</c> はローカル認識用（名称・位置を維持）。<c>Engine</c> がクラウドのときだけ <c>Cloud</c> を使う。</summary>
public sealed record RecognitionSettings(string Model, string Language, RecognitionEngine Engine, CloudRecognitionSettings Cloud)
{
    public RecognitionSettings(string model, string language) : this(model, language, RecognitionEngine.Local, CloudRecognitionSettings.Empty) { }
}

/// <summary>秘密情報は暗号文（Base64）だけを持つ。平文のプロパティは存在しない。</summary>
public sealed record CompatibleCleanupSettings(string BaseUrl, string? ApiKeyDpapi, string Model);

public sealed record ChatGptCleanupSettings(string Model);

public sealed record CleanupSettings(CleanupProvider Provider, int TimeoutSeconds, CompatibleCleanupSettings Compatible, ChatGptCleanupSettings ChatGpt);

public sealed record ProxySettings(ProxyMode Mode, string ManualUrl, string ManualUsername, string? ManualPasswordDpapi, ProxyManualAuth ManualAuth = ProxyManualAuth.Credentials);

/// <summary>
/// 投入経路（M10）。Auto は宛先クラスで決める（標準編集クラスは paste-message、カスタム描画は ctrl-v）。
/// ctrl-v / shift-insert / custom はクリップボードへ置いてからキーを送る。typing はクリップボードを使わず Unicode 文字入力で送る。
/// </summary>
public enum InsertionRoute { Auto, PasteMessage, CtrlV, ShiftInsert, Typing, Custom }

public static class InsertionRoutes
{
    private static readonly (InsertionRoute Route, string Name)[] Names =
    [
        (InsertionRoute.Auto, "auto"), (InsertionRoute.PasteMessage, "paste-message"), (InsertionRoute.CtrlV, "ctrl-v"),
        (InsertionRoute.ShiftInsert, "shift-insert"), (InsertionRoute.Typing, "typing"), (InsertionRoute.Custom, "custom"),
    ];

    public static IReadOnlyList<string> AllNames { get; } = Names.Select(n => n.Name).ToArray();

    public static string Name(InsertionRoute r) => Names.First(n => n.Route == r).Name;

    public static bool TryParse(string? name, out InsertionRoute route)
    {
        foreach (var n in Names) if (n.Name == name) { route = n.Route; return true; }
        route = InsertionRoute.Auto;
        return false;
    }
}

/// <summary>アプリ別の経路上書き。Process は exe のベース名（拡張子なし）。照合は大文字小文字を区別しない。</summary>
public sealed record AppRoute(string Process, InsertionRoute Route);

/// <summary>
/// 投入の設定。<c>Method</c> は旧版の項目（維持）。<c>CustomKey</c> は custom 経路で送るキー組み合わせ（未設定は null）。
/// <c>AppRoutes</c> はアプリ別の上書き（経路の決定は「アプリ別上書き → auto のクラス判定」）。
/// </summary>
public sealed record InsertionSettings(InsertionMethod Method, Hotkey? CustomKey, IReadOnlyList<AppRoute> AppRoutes)
{
    public InsertionSettings(InsertionMethod method) : this(method, null, []) { }

    /// <summary>プロセス名（拡張子の有無・大文字小文字は問わない）に上書きがあれば、その経路。無ければ null。</summary>
    public InsertionRoute? OverrideFor(string? process)
    {
        var key = NormalizeProcess(process);
        if (key.Length == 0) return null;
        foreach (var a in AppRoutes)
            if (string.Equals(NormalizeProcess(a.Process), key, StringComparison.OrdinalIgnoreCase)) return a.Route;
        return null;
    }

    /// <summary>前後の空白と、末尾の .exe を取り除く。</summary>
    public static string NormalizeProcess(string? name)
    {
        var t = (name ?? "").Trim();
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4].TrimEnd() : t;
    }

    public bool Equals(InsertionSettings? other) =>
        other is not null && Method == other.Method && Equals(CustomKey, other.CustomKey) && AppRoutes.SequenceEqual(other.AppRoutes);

    public override int GetHashCode() => HashCode.Combine(Method, CustomKey, AppRoutes.Count);
}

public sealed record UpdateSettings(DateTimeOffset? LastCheckAttemptUtc, DateTimeOffset? LastCheckSuccessUtc);

/// <summary>
/// Hane 連携（設計 §4.6）。<c>Enabled</c> はアプリ単位の連携（既定 OFF）、<c>InsertRawFirst</c> は連携内の「原文を先に挿入」（既定 ON、推奨）。
/// 文書の抜粋・用語・要約の外部送信は W-3 以降で別の許可として足す。
/// </summary>
public sealed record HaneSettings(bool Enabled, bool InsertRawFirst)
{
    public static HaneSettings Default { get; } = new(false, true);
}

/// <summary>設計 §8.1 の config.json（schema 1）。</summary>
public sealed record AppSettings(
    int SchemaVersion,
    Hotkey Hotkey,
    string? MicrophoneId,
    RecognitionSettings Recognition,
    CleanupSettings Cleanup,
    ProxySettings Proxy,
    bool Autostart,
    InsertionSettings Insertion,
    UpdateSettings Updates,
    HaneSettings Hane)
{
    public const int CurrentSchemaVersion = 1;
    public const int CleanupTimeoutSeconds = 15;
    public const string Language = "ja";

    /// <summary>D-4 確定値: Win+J（司令塔が対話デスクトップで登録成功を実測）。</summary>
    public static Hotkey DefaultHotkey { get; } = HotkeyFormat.WinJ;

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        DefaultHotkey,
        null,
        new RecognitionSettings(ModelIds.Small, Language),
        new CleanupSettings(CleanupProvider.Off, CleanupTimeoutSeconds, new CompatibleCleanupSettings("", null, ""), new ChatGptCleanupSettings("")),
        new ProxySettings(ProxyMode.Environment, "", "", null),
        false,
        new InsertionSettings(InsertionMethod.Clipboard),
        new UpdateSettings(null, null),
        HaneSettings.Default);
}

public sealed record SettingsIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}
