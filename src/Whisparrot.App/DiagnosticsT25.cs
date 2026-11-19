using System.IO;
using System.Diagnostics;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// クラウド文字起こし（T25）の非対話診断。音声の外部送信は、利用者がこのコマンドを実行したときだけ起きる（自動起動・設定保存では実行しない）。
///   --write-transcription-config &lt;config.json&gt; [--url U] [--model M] [--key-env NAME] [--engine cloud|local]
///        既存の config.json を読み、recognition の engine / cloud だけを更新する（他の項目・未知 / 未来 schema は変更しない）。
///        キーは環境変数から読み、DPAPI で暗号化して保存する。値は表示しない
///   --transcription-probe --config &lt;config.json&gt; --wav &lt;16 kHz mono PCM16&gt; [--show-text]
///        クラウドが選択された設定で、実際の準備・認識経路（CloudRecognitionConfig → CloudTranscriber → NetworkContext）を 1 回だけ通す。
///        既定の出力は分類と時間と文字数だけ。本文は --show-text を指定したときだけ出す。前景アプリへは投入しない
/// </summary>
internal static partial class Diagnostics
{
    public static int WriteTranscriptionConfig(string[] args)
    {
        var path = args.Length > 1 ? args[1] : throw new ArgumentException("config.json のパスが必要です");
        var engine = (Arg(args, "--engine") ?? "cloud") switch
        {
            "local" => RecognitionEngine.Local,
            "cloud" => RecognitionEngine.Cloud,
            _ => throw new ArgumentException("--engine は cloud か local です"),
        };
        var store = new JsonSettingsStore(path);
        var settings = new SettingsService(store);
        if (!settings.LoadResult.Usable || !settings.IsWritable)
        {
            Console.WriteLine("write=REFUSED reason=config_not_writable status=" + settings.LoadResult.Status);
            return 1;
        }
        var keyEnv = Arg(args, "--key-env");
        var key = keyEnv is null ? null : Environment.GetEnvironmentVariable(keyEnv);
        if (keyEnv is not null && string.IsNullOrEmpty(key))
        {
            Console.WriteLine("write=REFUSED reason=key_env_empty");
            return 1;
        }
        var protector = new DpapiSecretProtector();
        settings.Update(s =>
        {
            var c = s.Recognition.Cloud;
            if (Arg(args, "--url") is { } url) c = c with { BaseUrl = url };
            if (Arg(args, "--model") is { } model) c = c with { Model = model };
            if (keyEnv is not null) c = c with { ApiKeyDpapi = protector.Protect(key) };
            return s with { Recognition = s.Recognition with { Engine = engine, Cloud = c } };
        });
        var now = settings.Current.Recognition;
        Console.WriteLine($"written engine={now.Engine.ToString().ToLowerInvariant()} key={(now.Cloud.ApiKeyDpapi is null ? "none" : "dpapi")}");
        return 0;
    }

    public static int TranscriptionProbe(string[] args)
    {
        var configPath = Arg(args, "--config") ?? throw new ArgumentException("--config が必要です");
        var wav = Arg(args, "--wav") ?? throw new ArgumentException("--wav が必要です");
        var showText = args.Contains("--show-text");

        var protector = new DpapiSecretProtector();
        var settings = new SettingsService(new JsonSettingsStore(configPath));
        if (!settings.LoadResult.Usable)
        {
            Console.WriteLine("recognition_outcome=config_error failure_kind=Config");
            return 1;
        }
        if (settings.Current.Recognition.Engine != RecognitionEngine.Cloud)
        {
            Console.WriteLine("recognition_outcome=not_cloud failure_kind=NotCloud injected_count=0");
            return 2;
        }

        PcmAudio pcm;
        try { pcm = WavFileRecorder.ReadPcm(wav); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine("recognition_outcome=input_error failure_kind=Wav");
            return 2;
        }

        // 実際の準備（DPAPI 復号・URL / モデル検証）。ネットワークにはまだ触れない。
        var t0 = Stopwatch.StartNew();
        var config = CloudRecognitionConfig.Prepare(settings.Current.Recognition, protector);
        if (config is not CloudRecognitionConfig.Ready ready)
        {
            Console.WriteLine("state=Unconfigured");
            Console.WriteLine($"elapsed_ms={t0.ElapsedMilliseconds}");
            Console.WriteLine("recognition_outcome=unconfigured failure_kind=NotConfigured http_status=- injected_count=0 text_chars=0");
            return 1;
        }
        Console.WriteLine($"state=Recognizing destination_host={CompatibleEndpointUrl.DestinationHost(ready.Endpoint)} audio_ms={(int)pcm.Duration.TotalMilliseconds}");

        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Current.Proxy, protector), ProxyEnvironment.Capture());
        var transcriber = new CloudTranscriber(network, ready.Endpoint, ready.Model, ready.ApiKey, language: ready.Language);
        var sw = Stopwatch.StartNew();
        TranscriptionResult r;
        try { r = transcriber.TranscribeAsync(pcm, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"elapsed_ms={sw.ElapsedMilliseconds}");
            Console.WriteLine("recognition_outcome=cancelled failure_kind=Cancelled http_status=- injected_count=0 text_chars=0");
            return 1;
        }
        Console.WriteLine($"state=Idle elapsed_ms={sw.ElapsedMilliseconds}");
        // injected_count は常に 0（probe は前景アプリへ投入しない。投入は --pipeline-file の記録用 injector で別に検査する）。
        if (r.IsSuccess)
        {
            var empty = r.Text!.Length == 0;
            Console.WriteLine($"recognition_outcome={(empty ? "empty" : "success")} failure_kind=None http_status=2xx injected_count=0 text_chars={r.Text.Length}");
            if (showText) Console.WriteLine("text=" + r.Text);
            return 0;
        }
        Console.WriteLine($"recognition_outcome=failed failure_kind={r.Failure} http_status={(r.HttpStatus is { } s ? s : "-")} injected_count=0 text_chars=0");
        return 1;
    }
}
