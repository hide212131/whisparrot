namespace Whisparrot.Core.Hane;

/// <summary>投入先がトップレベルのウィンドウとして特定できるとき、実装が併せて公開する（Hane 連携が窓と PID で相手を選ぶ。設計 §2）。</summary>
public interface IWindowTarget
{
    long Hwnd { get; }
    uint Pid { get; }
}

/// <summary>録音開始時に記録した前景のトップレベルウィンドウ。ファイルパス・タイトル・実行ファイル名の推測では相手を選ばない。</summary>
public sealed record HaneWindow(long Hwnd, uint Pid);

/// <summary>Hane 連携の失敗の分類。本文・パス・例外文字列は持たない（ログ・診断に出してよい）。</summary>
public enum HaneFailure
{
    None,
    NoDescriptor,
    BadDescriptor,
    /// <summary>記述子の PID・作成時刻が現在のプロセスと一致しない（異常終了の残骸）。</summary>
    StaleInstance,
    ConnectFailed,
    /// <summary>パイプの接続相手が記述子の PID と一致しない。トークンは送らない。</summary>
    ServerMismatch,
    AuthRejected,
    UnsupportedVersion,
    Protocol,
    Timeout,
    TooLarge,
    /// <summary>文脈を取得した相手と別のインスタンスだった（再起動など）。</summary>
    InstanceChanged,
    /// <summary>Hane が <c>error</c> を返した。<see cref="HaneResult{T}.Code"/> に固定コード。</summary>
    Remote,
}

/// <summary>失敗時の編集の確定状況（設計 §3.4）。<see cref="NotApplied"/> は未適用が確かなとき、<see cref="Unknown"/> は適用済みかもしれないとき。</summary>
public enum HaneEffect { NotApplied, Unknown }

public sealed record HaneResult<T>
{
    private HaneResult() { }

    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public HaneFailure Failure { get; private init; }

    /// <summary>Hane の固定エラーコード（<see cref="HaneFailure.Remote"/> のとき）。</summary>
    public string? Code { get; private init; }

    public HaneEffect Effect { get; private init; }

    public static HaneResult<T> Success(T value) => new() { Ok = true, Value = value };

    public static HaneResult<T> Fail(HaneFailure failure, string? code = null, HaneEffect effect = HaneEffect.NotApplied) =>
        new() { Failure = failure, Code = code, Effect = effect };

    public override string ToString() => Ok ? "ok" : $"{Failure}{(Code is null ? "" : ":" + Code)}/{Effect}";
}

public sealed record HaneSelection(long AnchorByte, long ActiveByte);

public sealed record HaneVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";
}

/// <param name="Features">機能フラグ（応答にあったものだけ。名前は診断表示用で、動作の分岐には使わない）。</param>
public sealed record HaneHello(HaneVersion Version, string InstanceId, IReadOnlyList<string> Features);

/// <summary>文脈取得の追跡結果（当該クライアントが整文確定待ちにしている範囲）。座標は UTF-8 バイトの半開区間。</summary>
public sealed record HanePendingRange(string RangeHandle, bool Tracked, long StartByte, long EndByte);

/// <summary>
/// <c>context.get</c> の結果。文書の本文を含むためメモリ限りで扱い、ログ・診断に出さない（設計 §5）。
/// <see cref="ToString"/> は大きさだけを返す。
/// </summary>
public sealed record HaneContext(
    string InstanceId,
    string DocumentId,
    string Revision,
    long CursorByte,
    HaneSelection Selection,
    string Title,
    bool IsUntitled,
    string FrontMatterStatus,
    string? FrontMatter,
    string Before,
    string After,
    bool BeforeTruncated,
    bool AfterTruncated,
    long? BeforeStartByte,
    long? AfterStartByte,
    IReadOnlyList<HanePendingRange> PendingRanges)
{
    public override string ToString() => $"HaneContext(before={Before.Length}, after={After.Length}, pending={PendingRanges.Count})";
}

public sealed record HaneContextRequest(int BeforeChars = 1200, int AfterChars = 200, IReadOnlyList<string>? PendingRangeHandles = null)
{
    public const int MaxPendingHandles = 2;
}

/// <summary><c>edit.insert</c> の成功。<see cref="RangeHandle"/> は後の差し替えに使う不透明 ID（秘密ではないがログに出さない）。</summary>
public sealed record HaneInserted(
    string InstanceId,
    string DocumentId,
    string InsertedRevision,
    long StartByte,
    long EndByte,
    string RangeHandle,
    string ExpectedTextHash,
    long ExpiresAtTick)
{
    public override string ToString() => $"HaneInserted(range={StartByte}..{EndByte})";
}

/// <param name="UndoMode"><c>merged</c> / <c>separate</c> / <c>none</c>（応答にあれば）。</param>
public sealed record HaneReplaced(string DocumentId, string Revision, long StartByte, long EndByte, string? UndoMode);

/// <summary>Hane への窓口。操作ごとに接続し、認証・版交渉を済ませてから要求する。例外を投げず、失敗は <see cref="HaneResult{T}"/> で返す。</summary>
public interface IHaneClient
{
    /// <summary>録音開始時の対象・文脈の取得。期限は <paramref name="ct"/>（呼び出し側が予算を決める）。編集はしない。</summary>
    Task<HaneResult<HaneContext>> GetContextAsync(HaneWindow window, HaneContextRequest request, CancellationToken ct);

    /// <summary>文脈を取得した窓・文書・版・選択へ原文を挿入する。応答喪失は同じ操作 ID で一度照会する（設計 §4.5）。</summary>
    Task<HaneResult<HaneInserted>> InsertAsync(HaneWindow window, HaneContext context, string text, CancellationToken ct);

    /// <summary>挿入した範囲を、未変更のときだけ整文結果で差し替える。応答喪失は同じ操作 ID で一度照会する。</summary>
    Task<HaneResult<HaneReplaced>> ReplaceAsync(HaneWindow window, HaneInserted inserted, string replacement, CancellationToken ct);
}

/// <summary>1 セッション分の Hane 連携の構え。録音開始時に確定する（整文の <c>PolishSetup</c> と同じ作法）。</summary>
public abstract record HaneSetup
{
    /// <summary>連携 OFF。Hane へは何も送らない。</summary>
    public sealed record Off : HaneSetup;

    public sealed record Ready(IHaneClient Client, bool InsertRawFirst) : HaneSetup;
}

public interface IHaneLink
{
    /// <summary>現在の設定から構えを作る。例外を投げない（投げた場合は Coordinator が <see cref="HaneSetup.Off"/> 扱いにする）。</summary>
    HaneSetup Prepare();
}
