using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Whisparrot.Infrastructure.Hane;

/// <summary>ワイヤ形式（設計 §2）: <c>u32 LE の長さ + UTF-8 JSON</c>。サイズは割当の前に検証する。</summary>
internal static class HaneWire
{
    public const int MaxFrameBytes = 256 * 1024;
    public const int MaxTextBytes = 32 * 1024;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed class FrameException(bool tooLarge) : Exception("frame")
    {
        public bool TooLarge { get; } = tooLarge;
    }

    public static async Task WriteAsync(Stream stream, JsonNode frame, CancellationToken ct)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(frame.ToJsonString(JsonOptions));
        if (json.Length > MaxFrameBytes) throw new FrameException(tooLarge: true);
        var buf = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)json.Length);
        json.CopyTo(buf, 4);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false); // 一回の書き込みにまとめる
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len == 0 || len > MaxFrameBytes) throw new FrameException(tooLarge: len > MaxFrameBytes);
        var body = new byte[len];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(body, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                ?? throw new FrameException(tooLarge: false);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new FrameException(tooLarge: false);
        }
    }

    public static int Utf8Length(string s) => System.Text.Encoding.UTF8.GetByteCount(s);
}
