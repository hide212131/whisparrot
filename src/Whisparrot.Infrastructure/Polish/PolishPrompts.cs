using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Polish;

/// <summary>
/// 整文の指示と、ユーザー入力の組み立て。互換・ChatGPT の両経路で共有する（設計 §4.3）。
/// 文脈がなければ従来の指示と発話そのもの。文脈があれば文脈付き専用の固定指示と、発話・前後の文・用語などを別フィールドに持つ JSON。
/// 文脈は指示へ文字列結合せず、常にユーザー入力側へ置く。
/// </summary>
public static class PolishPrompts
{
    private static readonly Lazy<string> Plain = new(() => Load("cleanup-instructions.txt"));
    private static readonly Lazy<string> WithContext = new(() => Load("cleanup-instructions-context.txt"));

    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>従来の発話単体の整文指示。</summary>
    public static string Instructions => Plain.Value;

    /// <summary>文脈付きの整文指示。</summary>
    public static string ContextInstructions => WithContext.Value;

    public static bool HasContext(PolishInput input) => input.Context is { IsEmpty: false };

    public static string InstructionsFor(PolishInput input) => HasContext(input) ? ContextInstructions : Instructions;

    /// <summary>ユーザー入力の本文。文脈なしは発話そのまま（従来の要求と同一）。</summary>
    public static string UserContentFor(PolishInput input)
    {
        if (input.Context is not { IsEmpty: false } c) return input.Utterance;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("utterance", input.Utterance);
            if (c.Before.Length > 0) w.WriteString("before", c.Before);
            if (c.After.Length > 0) w.WriteString("after", c.After);
            if (c.Title.Length > 0) w.WriteString("title", c.Title);
            WriteList(w, "metadata", c.Metadata);
            WriteList(w, "terms", c.Terms);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }

    private static string Load(string name)
    {
        using var s = typeof(PolishPrompts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} が埋め込まれていません");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n');
    }
}
