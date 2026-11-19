using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>
/// 録音開始時の設定から認識の構えを作る（クラウド文字起こし設計 §3.1: 設定変更は次のセッションから）。
/// local はローカル認識（既存の準備確認と認識）へ委譲し、cloud は URL / モデル / キーを復号・検証して不変の値を捕捉する。
/// 準備でネットワーク疎通・ワーカー起動・ローカルモデル取得をしない。設定値自体は変更しない。
/// </summary>
public sealed class SettingsRecognizer(
    Func<AppSettings> settings, ISecretProtector protector, NetworkContext network, IRecognizer local, TimeSpan? cloudDeadline = null) : IRecognitionSource
{
    public RecognitionSetup Prepare()
    {
        var recognition = settings().Recognition;
        if (recognition.Engine == RecognitionEngine.Local)
            return new FixedRecognitionSource(local).Prepare();

        switch (CloudRecognitionConfig.Prepare(recognition, protector))
        {
            case CloudRecognitionConfig.Ready ready:
                var deadline = cloudDeadline ?? CloudTranscriber.Deadline;
                var transcriber = new CloudTranscriber(network, ready.Endpoint, ready.Model, ready.ApiKey, deadline, ready.Language);
                return new RecognitionSetup.Ready(RecognitionEngine.Cloud, transcriber.RecognizeAsync, deadline);
            case CloudRecognitionConfig.Unconfigured u:
                return new RecognitionSetup.Unconfigured(u.Reason);
            default:
                return new RecognitionSetup.Unconfigured("クラウド認識の設定を読み込めません");
        }
    }
}
