using System.IO;
using System.Diagnostics;
using Whisparrot.Core;
using Whisparrot.Core.FileInput;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// <c>whisparrot transcribe &lt;入力ファイル&gt; [--out &lt;出力フォルダ&gt;] [--no-polish]</c>（ファイル入力）。
/// config.json の設定どおりに認識→整文を一回通し、結果をファイルへ書いて終了する。トレイ・ピル・投入・Hane は使わない。
/// 終了コード: 0 成功 / 2 引数 / 3 入力ファイル / 4 認識 / 5 整文。理由は標準エラーへ出す。
/// </summary>
internal static class TranscribeCommand
{
    public const string Name = "transcribe";

    public static int Run(string[] args, INativeResourceSource resources)
    {
        var options = TranscribeOptions.Parse(args.Skip(1).ToArray(), out var usageError);
        if (options is null)
        {
            Console.Error.WriteLine(usageError);
            Console.Error.WriteLine(TranscribeOptions.Usage);
            return 2;
        }

        var started = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var input = Path.GetFullPath(options.InputPath);
        var outDir = options.OutDirectory is { } o ? Path.GetFullPath(o) : Path.GetDirectoryName(input)!;
        var stem = TranscribeOutput.StemOf(input);

        var settingsService = new SettingsService(new JsonSettingsStore(AppPaths.ConfigPath));
        var load = settingsService.LoadResult;
        var settings = load.Usable ? settingsService.Current : null;

        // 入力の読み込み・変換（認識・整文とは別に時間を測る）。
        PcmAudio? pcm = null;
        TranscribeRun run;
        TimeSpan? decode = null;
        try
        {
            var sw = Stopwatch.StartNew();
            pcm = AudioFileDecoder.Read(input);
            decode = sw.Elapsed;
        }
        catch (Exception ex) when (ex is UnsupportedAudioException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // 入力そのものが使えない場合は何も書かない（存在しない入力の隣にフォルダやファイルを作らない）。
            Console.Error.WriteLine(ex.Message);
            return (int)TranscribeStatus.InputError;
        }

        if (settings is null) return Finish(Failed(TranscribeStatus.RecognitionFailed, load.Describe()), pcm);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var protector = new DpapiSecretProtector();
        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Proxy, protector), ProxyEnvironment.Capture());
        var models = new ModelManager(AppPaths.ModelsDirectory, ModelCatalog.Embedded(), network);
        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, AppInfo.Version, NativeRuntime.CurrentRid());
        var forceCpu = string.Equals(Environment.GetEnvironmentVariable("WHISPARROT_BACKEND"), "cpu", StringComparison.OrdinalIgnoreCase);
        using var local = new WhisperRecognizer(new WhisperRecognizerOptions(
            "", forceCpu ? ["cpu"] : ["vulkan", "cpu"], native.AvailableBackends,
            ModelResolver: () =>
            {
                var id = settings.Recognition.Model;
                return new ModelTarget(models.PathOf(id), models.CheckReady(id));
            }));
        var recognition = new SettingsRecognizer(() => settings, protector, network, local);
        var chatGpt = new ChatGptAuthService(network, new ChatGptTokenStore(AppPaths.ChatGptTokenPath, protector));
        var polisher = new SettingsTextPolisher(() => settings, protector, network, chatGpt);

        run = FileTranscriber.RunAsync(pcm, recognition, polisher, options.NoPolish, cts.Token).GetAwaiter().GetResult();
        return Finish(run, pcm);

        static TranscribeRun Failed(TranscribeStatus status, string error) =>
            new(status, error, null, null, null, null, PolishOutcome.Skipped, null, null);

        int Finish(TranscribeRun r, PcmAudio? audio)
        {
            var record = new TranscribeRecord(AppInfo.DisplayVersion, started, Path.GetFileName(input), AudioFileDecoder.FormatOf(input),
                audio?.Duration, decode, settings, !options.NoPolish, total.Elapsed);
            try
            {
                foreach (var path in TranscribeOutput.Write(outDir, stem, record, r)) Console.WriteLine(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"出力を書けません: {ex.Message}");
                return r.Status == TranscribeStatus.Ok ? 1 : (int)r.Status;
            }
            if (r.Error is not null) Console.Error.WriteLine(r.Error);
            return (int)r.Status;
        }
    }
}
