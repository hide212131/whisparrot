namespace Whisparrot.Core;

/// <summary>整文が使えなかった理由の分類。利用者向け表示とテスト用で、応答本文・URL・秘密は含めない。</summary>
public enum PolishFailure { Network, Timeout, Http, Auth, Empty, Malformed, Incomplete, TooLarge, Error }

/// <summary>設計 §7.1: 成功に使えるのは完了した非空の本文だけ。それ以外はすべて <see cref="Fallback"/>。</summary>
public sealed record PolishResult
{
    private PolishResult() { }

    public string? Text { get; private init; }
    public PolishFailure Failure { get; private init; }

    /// <summary>失敗の補足（例: "HTTP 500"）。応答本文や例外メッセージは入れない。</summary>
    public string? Detail { get; private init; }

    public bool IsSuccess => Text is not null;

    public static PolishResult Success(string text) => new() { Text = text };

    public static PolishResult Fallback(PolishFailure failure, string? detail = null) => new() { Failure = failure, Detail = detail };
}

/// <summary>1 セッション分の整文の構え。録音開始時に確定し、途中の設定変更は次のセッションから効く。</summary>
public abstract record PolishSetup
{
    /// <summary>整文 OFF。ネットワークを呼ばない。</summary>
    public sealed record Off : PolishSetup;

    /// <summary>整文を選んでいるが接続情報が足りない／使えない。整文を試みず原文を投入し、理由を知らせる。</summary>
    public sealed record Unconfigured(string Reason) : PolishSetup;

    /// <summary>整文を実行できる。<paramref name="Timeout"/> はキーの復号や接続を含む全体の期限。</summary>
    public sealed record Ready(Func<string, CancellationToken, Task<PolishResult>> PolishAsync, TimeSpan Timeout) : PolishSetup;
}

public interface ITextPolisher
{
    /// <summary>現在の設定から整文の構えを作る。例外を投げない（投げた場合は Coordinator が Unconfigured 扱いにする）。</summary>
    PolishSetup Prepare();
}
