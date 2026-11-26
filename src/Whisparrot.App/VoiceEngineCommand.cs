using System.IO.Pipes;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Infrastructure.Voice;

namespace Whisparrot.App;

/// <summary>
/// エンジンモードの起動入口（設計 §2.1 / P2）。親（Hane）が子として起動する。単一起動の mutex・グローバルホットキー・トレイ・UI・自己更新・自動起動登録・
/// 投入（前景取得・貼り付け）を持たない。録音・区間分割・認識・整文は常駐アプリと同じ部品を使う。
///   whisparrot --voice-engine --voice-handoff &lt;継承した匿名パイプのハンドル&gt;
/// 接続情報（専用パイプ名・トークン・親 PID）は、その匿名パイプから一度だけ読む。
/// </summary>
internal static class VoiceEngineCommand
{
    public const string Flag = "--voice-engine";
    private const string HandoffOption = "--voice-handoff";

    public static int Run(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, HandoffOption);
            if (i < 0 || i + 1 >= args.Length) return VoiceEngineHost.ExitHandshake;
            VoiceHandoff? handoff;
            using (var input = new AnonymousPipeClientStream(PipeDirection.In, args[i + 1]))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                handoff = VoiceHandoff.ReadAsync(input, timeout.Token).GetAwaiter().GetResult();
            }
            if (handoff is null) return VoiceEngineHost.ExitHandshake;

            // 設定は読み取り専用で開き、セッション開始のたびに固定し直す。
            var store = new JsonSettingsStore(AppPaths.ConfigPath, readOnly: true);
            var snapshot = store.Load().Settings;
            var protector = new DpapiSecretProtector();
            var network = new NetworkContext(() => ProxyConfig.FromSettings(snapshot.Proxy, protector), ProxyEnvironment.Capture());
            var chatGpt = new ChatGptAuthService(network, new ChatGptTokenStore(AppPaths.ChatGptTokenPath, protector));
            var polisher = new SettingsTextPolisher(() => snapshot, protector, network, chatGpt);
            // ローカル認識はエンジンモードの対応外（実時間性能とワーカー回収の検証待ち）。クラウド認識だけを通す。
            var recognition = new SettingsRecognizer(() => snapshot, protector, network, new UnsupportedLocalRecognizer());
            using var recorder = new WasapiRecorder(() => snapshot.MicrophoneId);
            using var coordinator = new SessionCoordinator(recorder, recognition, new NoInjector(), polisher, null, () => snapshot.Streaming);

            var host = new VoiceEngineHost(coordinator, new VoiceEngineOptions { BeforeCapture = () => snapshot = store.Load().Settings });
            return host.RunAsync(handoff, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return 1; // 静かに終了する（本文・秘密を出さない）
        }
    }

    private sealed class UnsupportedLocalRecognizer : IRecognizer
    {
        public string? CheckReady() => "エンジンモードではローカル認識を使えません";
        public Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>エンジンモードに投入はない。呼ばれたら失敗にする（区間経路は <see cref="ISegmentSink"/> へ出す）。</summary>
    private sealed class NoInjector : ITextInjector
    {
        public Task<InjectResult> InjectAsync(string text, CancellationToken ct) =>
            Task.FromResult(new InjectResult(InjectOutcome.Failed, "エンジンモードでは入力しません"));
    }
}
