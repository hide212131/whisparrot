namespace Whisparrot.Core;

/// <summary>
/// 文脈付き整文の入力に載せる文書の文脈（設計 §4.1）。本文を含むためメモリ限りで扱い、ログ・診断に出さない。
/// 生の Front Matter は持たず、許可リストから採ったメタデータだけを持つ。
/// </summary>
public sealed record PolishContext(string Before, string After, string Title, IReadOnlyList<string> Metadata, IReadOnlyList<string> Terms)
{
    public bool IsEmpty => Before.Length == 0 && After.Length == 0 && Title.Length == 0 && Metadata.Count == 0 && Terms.Count == 0;

    public override string ToString() => $"PolishContext(before={Before.Length}, after={After.Length}, terms={Terms.Count})";
}

/// <summary>整文の入力。<paramref name="Context"/> が null なら従来の発話だけの整文。</summary>
public sealed record PolishInput(string Utterance, PolishContext? Context = null)
{
    public override string ToString() => $"PolishInput(utterance={Utterance.Length}, context={Context?.ToString() ?? "none"})";
}
