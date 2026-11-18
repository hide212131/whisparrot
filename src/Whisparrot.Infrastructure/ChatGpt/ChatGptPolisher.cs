using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;

namespace Whisparrot.Infrastructure.ChatGpt;

/// <summary>
/// 設計 §7: ChatGPT 経由の整文。access token を Bearer にして公開 Responses API（<c>POST /v1/responses</c>）へ送る（非公開の backend-api は使わない）。
/// 固定の整文指示は <c>instructions</c>、認識テキストは <c>input</c> で、指示へ文字列結合しない。期限・フォールバック・失敗の補足の規律は
/// <see cref="CompatiblePolisher"/> と同じ（どの失敗も例外ではなく <see cref="PolishResult.Fallback"/>。利用者の取消だけが例外）。
/// </summary>
public sealed class ChatGptPolisher(NetworkContext network, ChatGptAuthService auth, string model, TimeSpan timeout)
{
    /// <summary>送るのは model / instructions / input / stream:false / store:false だけ（応答を ChatGPT 側へ保存させない）。</summary>
    public static byte[] BuildRequestBody(string model, string rawText)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("instructions", CompatiblePolisher.Instructions);
            w.WriteString("input", rawText);
            w.WriteBoolean("stream", false);
            w.WriteBoolean("store", false);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public async Task<PolishResult> PolishAsync(string rawText, CancellationToken ct)
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
                Content = new ByteArrayContent(BuildRequestBody(model, rawText)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return PolishResult.Fallback(PolishFailure.Auth, $"HTTP {status}");
            if (status is < 200 or > 299) return PolishResult.Fallback(PolishFailure.Http, $"HTTP {status}");

            if (response.Content.Headers.ContentLength is > CompatiblePolisher.MaxResponseBytes) return PolishResult.Fallback(PolishFailure.TooLarge);
            var body = await CompatiblePolisher.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            return body is null ? PolishResult.Fallback(PolishFailure.TooLarge) : ParseResponse(body);
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

    /// <summary>
    /// <c>status == "completed"</c> の応答の、最初の <c>message</c> 項目の <c>output_text</c> を取り出す。
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
