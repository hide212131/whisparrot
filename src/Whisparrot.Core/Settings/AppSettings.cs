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
public sealed record CloudRecognitionSettings(string BaseUrl, string? ApiKeyDpapi, string Model)
{
    public static CloudRecognitionSettings Empty { get; } = new("", null, "");

    /// <summary>全項目が既定（未設定）。設定ファイルには書き出さない。</summary>
    public bool IsEmpty => BaseUrl.Length == 0 && ApiKeyDpapi is null && Model.Length == 0;
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

public sealed record InsertionSettings(InsertionMethod Method);

public sealed record UpdateSettings(DateTimeOffset? LastCheckAttemptUtc, DateTimeOffset? LastCheckSuccessUtc);

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
    UpdateSettings Updates)
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
        new UpdateSettings(null, null));
}

public sealed record SettingsIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}
