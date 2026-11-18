using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>
/// クラウド認識の構え（設計 §3.1 / §4.2）。<see cref="Ready"/> は録音開始時に固定する不変の値で、キーは復号済みの平文を
/// この構えの寿命だけ持つ（<see cref="ToString"/> は秘密を出さない）。<see cref="Unconfigured"/> の理由は固定文言で、設定値・暗号文を含まない。
/// </summary>
public abstract record CloudRecognitionConfig
{
    public sealed record Unconfigured(string Reason) : CloudRecognitionConfig;

    public sealed record Ready : CloudRecognitionConfig
    {
        public Ready(Uri endpoint, string model, string? apiKey)
        {
            Endpoint = endpoint; Model = model; ApiKey = apiKey;
        }

        /// <summary>POST 先（<c>base_url</c> の末尾 <c>/</c> を除いて <c>/audio/transcriptions</c> を連結したもの）。</summary>
        public Uri Endpoint { get; }

        /// <summary>利用者の入力そのまま（大小文字変換・接頭辞追加をしない）。</summary>
        public string Model { get; }

        /// <summary>null は認証なし（正確な loopback 宛の空キーだけ）。</summary>
        public string? ApiKey { get; }

        public override string ToString() => $"Ready {{ Endpoint = {Endpoint}, Model = {Model}, ApiKey = {(ApiKey is null ? "(none)" : "***")} }}";
    }

    public const string TranscriptionPath = "/audio/transcriptions";

    /// <summary>
    /// 設定から構えを作る。ネットワーク・ワーカー・ローカルモデルには触れず、設定値は変更しない（復号不能でも暗号文を消さない）。
    /// 外部宛でキー未設定・復号不能は準備不能。正確な loopback 宛だけ空キーを許すが、保存済みキーが復号できないときは認証なしへ黙って切り替えない。
    /// </summary>
    public static CloudRecognitionConfig Prepare(RecognitionSettings recognition, ISecretProtector protector)
    {
        if (recognition.Engine != RecognitionEngine.Cloud) return new Unconfigured("認識先がクラウドに設定されていません");
        var c = recognition.Cloud;
        if (c.BaseUrl.Length == 0) return new Unconfigured("クラウド認識のベース URL が未設定です");
        if (CompatibleEndpointUrl.Validate(c.BaseUrl, out var uri) is not null || uri is null)
            return new Unconfigured("クラウド認識のベース URL が不正です");
        if (c.Model.Trim().Length == 0) return new Unconfigured("クラウド認識のモデル名が未設定です");
        if (c.Model.Any(char.IsControl)) return new Unconfigured("クラウド認識のモデル名が不正です");

        string? key = null;
        if (c.ApiKeyDpapi is not null)
        {
            // 暗号文があるのに復号できない、または空へ復号される場合は準備不能（loopback でも認証なしへ切り替えない）。
            if (!protector.TryUnprotect(c.ApiKeyDpapi, out key) || string.IsNullOrEmpty(key))
                return new Unconfigured("クラウド認識の API キーを復号できません。設定で入力し直してください");
            if (key.Any(char.IsControl)) return new Unconfigured("クラウド認識の API キーに使えない文字が含まれています");
        }
        else if (!CompatibleEndpointUrl.IsExactLoopback(uri))
        {
            return new Unconfigured("クラウド認識の API キーが未設定です");
        }

        var endpoint = new Uri(c.BaseUrl.TrimEnd('/') + TranscriptionPath, UriKind.Absolute);
        return new Ready(endpoint, c.Model, key);
    }
}
