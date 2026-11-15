using System.Buffers.Binary;
using System.Text.Json;

namespace Whisparrot.Infrastructure.Recognition;

internal enum FrameType : byte
{
    Hello = 1,    // 子→親: 起動 nonce（16 バイト）
    Ready = 2,    // 子→親: モデルロード完了。JSON(ReadyInfo)
    Request = 3,  // 親→子: [int32 ヘッダ長][JSON(RequestHeader)][float32 PCM]
    Result = 4,   // 子→親: JSON(ResultInfo)
    Error = 5,    // 子→親: JSON(ErrorInfo)
    Shutdown = 6, // 親→子
}

internal sealed record ReadyInfo(string Backend, string Library, string Detail);
internal sealed record RequestHeader(string Language, int Threads, int SampleCount);
internal sealed record ResultInfo(string Text, string Backend);
internal sealed record ErrorInfo(string Message);

/// <summary>
/// 親子のフレーム: [int32 LE ペイロード長][byte 種別][ペイロード]。PCM / 結果 / 実行方式だけを運ぶ。
/// 長さ上限と種別を検証し、不正なら <see cref="InvalidDataException"/>。
/// </summary>
internal static class WorkerProtocol
{
    /// <summary>1 フレームの上限。float32 16 kHz で約 70 分相当。</summary>
    public const int MaxPayload = 1 << 28;

    public static async Task WriteAsync(Stream s, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new InvalidDataException("フレームが大きすぎます");
        var head = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(head, payload.Length);
        head[4] = (byte)type;
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        if (payload.Length > 0) await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(Stream s, FrameType type, T value, CancellationToken ct) =>
        WriteAsync(s, type, JsonSerializer.SerializeToUtf8Bytes(value), ct);

    public static async Task<(FrameType Type, byte[] Payload)> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[5];
        await ReadExactAsync(s, head, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        var type = (FrameType)head[4];
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"フレーム長が不正です ({len})");
        if (!Enum.IsDefined(type)) throw new InvalidDataException($"フレーム種別が不正です ({head[4]})");
        var payload = new byte[len];
        await ReadExactAsync(s, payload, ct).ConfigureAwait(false);
        return (type, payload);
    }

    public static T ParseJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("JSON が空です");

    public static byte[] BuildRequest(RequestHeader header, float[] samples)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        var buf = new byte[4 + json.Length + samples.Length * sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(buf, json.Length);
        json.CopyTo(buf, 4);
        Buffer.BlockCopy(samples, 0, buf, 4 + json.Length, samples.Length * sizeof(float));
        return buf;
    }

    public static (RequestHeader Header, float[] Samples) ParseRequest(byte[] payload)
    {
        if (payload.Length < 4) throw new InvalidDataException("リクエストが短すぎます");
        var jsonLen = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (jsonLen < 0 || jsonLen > payload.Length - 4) throw new InvalidDataException("ヘッダ長が不正です");
        var header = JsonSerializer.Deserialize<RequestHeader>(payload.AsSpan(4, jsonLen))
                     ?? throw new InvalidDataException("ヘッダが空です");
        var pcmBytes = payload.Length - 4 - jsonLen;
        if (pcmBytes != header.SampleCount * sizeof(float) || header.SampleCount < 0)
            throw new InvalidDataException("PCM 長がヘッダと一致しません");
        var samples = new float[header.SampleCount];
        Buffer.BlockCopy(payload, 4 + jsonLen, samples, 0, pcmBytes);
        return (header, samples);
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("相手が接続を閉じました");
            read += n;
        }
    }
}
