using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>設定画面のクラウド認識欄が使う、画面に依存しない判定（クラウド文字起こし設計 §4.3 / §9.1）。</summary>
public static class CloudConnectionForm
{
    /// <summary>「整文の互換接続からコピー」の結果。<see cref="KeyCipher"/> は元の暗号文（複写。復号して再暗号化しない）で、元にキーがなければ null。</summary>
    public sealed record CopyResult(string? Error, string BaseUrl = "", string? KeyCipher = null)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// 保存済みの整文の互換接続から URL とキーだけを複写する（整文モデルはコピーしない。以後は連動しない）。
    /// 元が未設定、または保存済みキーが復号できないときはコピーを成立させない。
    /// </summary>
    public static CopyResult CopyFromCleanup(CompatibleCleanupSettings source, ISecretProtector protector)
    {
        if (source.BaseUrl.Length == 0) return new CopyResult("整文の互換接続が未設定のため、コピーできません。");
        if (source.ApiKeyDpapi is not null && (!protector.TryUnprotect(source.ApiKeyDpapi, out var key) || string.IsNullOrEmpty(key)))
            return new CopyResult("整文の API キーを復号できないため、コピーできません。キーを入力し直してください。");
        return new CopyResult(null, source.BaseUrl, source.ApiKeyDpapi);
    }

    /// <summary>入力欄の URL から実際の要求先（<c>https://host/v1/audio/transcriptions</c>）を返す。URL が不正なら null。</summary>
    public static string? RequestTarget(string baseUrl) =>
        CompatibleEndpointUrl.Validate(baseUrl, out var uri) is null && uri is not null
            ? baseUrl.TrimEnd('/') + CloudRecognitionConfig.TranscriptionPath
            : null;

    /// <summary>入力中の設定でクラウド認識を始められない理由（固定文言）。始められるなら null。ネットワークには触れない。</summary>
    public static string? NotReadyReason(RecognitionSettings recognition, ISecretProtector protector) =>
        CloudRecognitionConfig.Prepare(recognition with { Engine = RecognitionEngine.Cloud }, protector) is CloudRecognitionConfig.Unconfigured u ? u.Reason : null;
}
