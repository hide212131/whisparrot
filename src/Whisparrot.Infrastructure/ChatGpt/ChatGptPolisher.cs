using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;

namespace Whisparrot.Infrastructure.ChatGpt;

/// <summary>
/// 設計 §7: ChatGPT 経由の整文。access token を Bearer にして公開 Responses API（<c>POST /v1/responses</c>）へ送る（非公開の backend-api は使わない）。
/// 固定の整文指示は <c>instructions</c>、認識テキストは user メッセージ配列の <c>input</c> で、指示へ文字列結合しない。
/// 公式 token-sharing の要件どおり <c>stream:true</c> + <c>store:false</c> で送り、SSE の <c>response.completed</c> だけを成功とする。期限・フォールバック・失敗の補足の規律は
/// <see cref="CompatiblePolisher"/> と同じ（どの失敗も例外ではなく <see cref="PolishResult.Fallback"/>。利用者の取消だけが例外）。
/// </summary>
public sealed class ChatGptPolisher(NetworkContext network, ChatGptAuthService auth, string model, TimeSpan timeout)
{
    /// <summary>送るのは model / instructions / input（user メッセージの配列）/ stream:true / store:false だけ（応答を ChatGPT 側へ保存させない）。</summary>
    public static byte[] BuildRequestBody(string model, string rawText) => BuildRequestBody(model, new PolishInput(rawText));

    /// <summary>文脈があれば文脈付きの固定指示（<c>instructions</c>）と文脈 JSON の user content、なければ素の整文要求。<c>stream:true</c> / <c>store:false</c> は常に維持する。</summary>
    public static byte[] BuildRequestBody(string model, PolishInput input)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("instructions", PolishPrompts.InstructionsFor(input));
            w.WriteStartArray("input");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", PolishPrompts.UserContentFor(input));
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("stream", true);
            w.WriteBoolean("store", false);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public Task<PolishResult> PolishAsync(string rawText, CancellationToken ct) => PolishAsync(new PolishInput(rawText), ct);

    public async Task<PolishResult> PolishAsync(PolishInput input, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout); // token 更新を含む全体の期限
        try
        {
            var token = await auth.GetAccessTokenAsync(deadline.Token).ConfigureAwait(false);
            if (token.Token is null) return PolishResult.Fallback(token.Failure, token.Detail);

            using var client = network.CreateClient(NetworkPurpose.Cleanup, timeout: Timeout.InfiniteTimeSpan);
            using var request = new HttpRequestMessage(HttpMethod.Post, auth.Endpoints.ResponsesUrl)
            {
                Content = new ByteArrayContent(BuildRequestBody(model, input)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return PolishResult.Fallback(PolishFailure.Auth, $"HTTP {status}");
            if (status is < 200 or > 299) return PolishResult.Fallback(PolishFailure.Http, $"HTTP {status}");

            var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await ReadEventStreamAsync(stream, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return PolishResult.Fallback(PolishFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return PolishResult.Fallback(PolishFailure.Network);
        }
        catch (Exception ex) when (ex is NetworkConfigurationException or NetworkPolicyException)
        {
            return PolishResult.Fallback(PolishFailure.Network);
        }
        catch
        {
            return PolishResult.Fallback(PolishFailure.Error);
        }
    }

    private const int MaxErrorDetail = 200;
    private const int MaxMalformedEvents = 3;

    /// <summary>
    /// SSE を読み、<c>response.output_text.delta</c> を連結する。<c>response.completed</c> で成功（空白だけは Empty）、
    /// <c>response.failed</c> / <c>response.incomplete</c> / 終端なしの終了 / 読めないイベントが続く場合は失敗。
    /// </summary>
    internal static async Task<PolishResult> ReadEventStreamAsync(Stream stream, CancellationToken ct)
    {
        await using var _ = stream.ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var data = new StringBuilder();
        var sawDelta = false;
        var malformed = 0;
        long bytes = 0;
        long textBytes = 0;

        PolishResult? Dispatch()
        {
            if (data.Length == 0) return null;
            var payload = data.ToString();
            data.Clear();
            if (payload == "[DONE]") return null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String)
                    return ++malformed >= MaxMalformedEvents ? PolishResult.Fallback(PolishFailure.Malformed) : null;
                malformed = 0;
                switch (tp.GetString())
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                        {
                            sawDelta = true;
                            var piece = d.GetString() ?? "";
                            textBytes += Encoding.UTF8.GetByteCount(piece);
                            if (textBytes > CompatiblePolisher.MaxResponseBytes) return PolishResult.Fallback(PolishFailure.TooLarge);
                            text.Append(piece);
                        }
                        return null;
                    case "response.completed":
                        if (!sawDelta)
                        {
                            if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object)
                                return PolishResult.Fallback(PolishFailure.Empty);
                            return ParseResponse(Encoding.UTF8.GetBytes(resp.GetRawText()));
                        }
                        var final = text.ToString().Trim();
                        return final.Length == 0 ? PolishResult.Fallback(PolishFailure.Empty) : PolishResult.Success(final);
                    case "response.failed":
                        return PolishResult.Fallback(PolishFailure.Error, FailureDetail(root));
                    case "response.incomplete":
                        return PolishResult.Fallback(PolishFailure.Incomplete);
                    default:
                        return null; // created / in_progress / output_item.* など
                }
            }
            catch (JsonException)
            {
                return ++malformed >= MaxMalformedEvents ? PolishResult.Fallback(PolishFailure.Malformed) : null;
            }
        }

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            bytes += line.Length + 1;
            if (bytes > CompatiblePolisher.MaxResponseBytes * 4L) return PolishResult.Fallback(PolishFailure.TooLarge);
            if (line.Length == 0)
            {
                if (Dispatch() is { } r) return r;
                continue;
            }
            if (line[0] == ':') continue; // コメント行
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line[5..].StartsWith(' ') ? 6 : 5));
            }
        }
        if (Dispatch() is { } last) return last; // 末尾の空行なしで終わった最後のイベント
        return PolishResult.Fallback(PolishFailure.Incomplete, "stream ended without a terminal event");
    }

    /// <summary><c>response.error.code</c> / <c>message</c> を 200 字までに切って補足にする（token 等は応答に載らない前提だが長さは制限する）。</summary>
    private static string? FailureDetail(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object
            || !resp.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object) return null;
        var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var detail = string.Join(": ", new[] { code, message }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (detail.Length == 0) return null;
        detail = detail.ReplaceLineEndings(" ");
        return detail.Length <= MaxErrorDetail ? detail : detail[..MaxErrorDetail];
    }

    /// <summary>
    /// <c>response.completed</c> 内の response（<c>status == "completed"</c>）の、最初の <c>message</c> 項目の <c>output_text</c> を取り出す。
    /// 未完了・失敗・拒否（refusal）・ツール呼出し・空白だけ・JSON でない応答は成功扱いしない。reasoning 項目は無視する。
    /// </summary>
    public static PolishResult ParseResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return PolishResult.Fallback(PolishFailure.Malformed);
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null) return PolishResult.Fallback(PolishFailure.Malformed);
            var completed = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "completed";
            if (!completed) return PolishResult.Fallback(PolishFailure.Incomplete);
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return PolishResult.Fallback(PolishFailure.Malformed);

            string? text = null;
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    return PolishResult.Fallback(PolishFailure.Malformed);
                switch (type.GetString())
                {
                    case "reasoning":
                        continue;
                    case "message":
                        if (text is not null) continue; // 複数の message は連結しない（先頭だけ）
                        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return PolishResult.Fallback(PolishFailure.Malformed);
                        var sb = new StringBuilder();
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var pt) || pt.ValueKind != JsonValueKind.String)
                                return PolishResult.Fallback(PolishFailure.Malformed);
                            if (pt.GetString() != "output_text") return PolishResult.Fallback(PolishFailure.Malformed); // refusal など
                            if (!part.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return PolishResult.Fallback(PolishFailure.Malformed);
                            sb.Append(t.GetString());
                        }
                        text = sb.ToString();
                        break;
                    default:
                        return PolishResult.Fallback(PolishFailure.Malformed); // function_call などのツール呼出し
                }
            }
            if (text is null) return PolishResult.Fallback(PolishFailure.Empty);
            text = text.Trim();
            return text.Length == 0 ? PolishResult.Fallback(PolishFailure.Empty) : PolishResult.Success(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return PolishResult.Fallback(PolishFailure.Malformed);
        }
    }
}
