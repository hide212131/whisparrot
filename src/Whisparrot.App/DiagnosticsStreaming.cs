using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// ストリーミング入力（S-1〜S-3）の非対話診断。マイクと実アプリへの投入は使わず、次を本物のまま通す:
/// SessionCoordinator のストリーミング経路 / 区間の切り出し（SegmentSplitter）/ クラウド認識（CloudRecognitionConfig → CloudTranscriber → NetworkContext → WAV 化 → HTTP）。
/// 音声は WAV を連続録音として流し、認識先はこのコマンドが起こすローカルの擬似サーバー（127.0.0.1）で、各区間の音声が元のどの位置かを照合して返す。
///   --streaming-probe &lt;a.wav&gt; [b.wav ...] [--interval N] [--speed X] [--stop-at-sec S] [--cancel-at-sec S]
///        [--slow-ms M]（奇数番の区間の応答を遅らせ、完了順を逆転させる）[--polish]（整文のモックも逆順に遅らせる）[--hang-seq K]（K 番の区間の応答を返さない）
///   出力は区間の入力時刻・順序判定・件数。本文は区間の識別子だけ（音声の内容は出さない）
/// </summary>
internal static partial class Diagnostics
{
    public static int StreamingProbe(string[] args)
    {
        string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var wavs = new List<string>();
        for (var i = 1; i < args.Length && !args[i].StartsWith("--"); i++) wavs.Add(args[i]);
        if (wavs.Count == 0) throw new ArgumentException("WAV が必要です");
        var interval = int.Parse(Opt("--interval") ?? "8");
        var speed = double.Parse(Opt("--speed") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var stopAt = Opt("--stop-at-sec") is { } s1 ? double.Parse(s1, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
        var cancelAt = Opt("--cancel-at-sec") is { } s2 ? double.Parse(s2, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
        var slowMs = int.Parse(Opt("--slow-ms") ?? "0");
        var hangSeq = Opt("--hang-seq") is { } h ? int.Parse(h) : -1;
        var polish = args.Contains("--polish");

        var source = wavs.SelectMany(p => WavFileRecorder.ReadPcm(p).Samples).Select(f => (short)Math.Round(f * 32768f)).ToArray();
        var segSamples = interval * PcmAudio.SampleRate;
        var t0 = Stopwatch.StartNew();
        string Ts() => $"[{t0.ElapsedMilliseconds,7} ms]";

        // 擬似のクラウド認識サーバー: 受け取った WAV の先頭が元音声のどの区間かを照合して、その識別子を返す。
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = 0;
        var inFlight = 0;
        var peakInFlight = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    int p;
                    while (now > (p = Volatile.Read(ref peakInFlight))) Interlocked.CompareExchange(ref peakInFlight, now, p);
                    try
                    {
                        using var ms = new MemoryStream();
                        await ctx.Request.InputStream.CopyToAsync(ms);
                        var body = ms.ToArray();
                        var riff = body.AsSpan().IndexOf("RIFF"u8);
                        var data = body.AsSpan(riff + 44);
                        var first = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data[..(64 * 2)]).ToArray();
                        var seq = -1;
                        for (var k = 0; k * segSamples < source.Length; k++)
                        {
                            if (source.AsSpan(k * segSamples, Math.Min(64, source.Length - k * segSamples)).SequenceEqual(first.AsSpan(0, Math.Min(64, source.Length - k * segSamples)))) { seq = k + 1; break; }
                        }
                        var durationSec = (body.Length - riff - 44) / 2 / 16000.0;
                        Interlocked.Increment(ref served);
                        Console.WriteLine($"{Ts()} server_request seq={seq} audio={durationSec:0.0}s");
                        if (seq == hangSeq) await Task.Delay(Timeout.Infinite, CancellationToken.None);
                        if (slowMs > 0) await Task.Delay(seq % 2 == 1 ? slowMs : 100);
                        var reply = Encoding.UTF8.GetBytes($"{{\"text\":\"区間{seq:00}\"}}");
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(reply);
                        ctx.Response.Close();
                        Console.WriteLine($"{Ts()} server_reply seq={seq}");
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                    finally { Interlocked.Decrement(ref inFlight); }
                });
            }
        });

        var protector = new DpapiSecretProtector();
        var settings = AppSettings.Default with
        {
            Recognition = AppSettings.Default.Recognition with
            {
                Engine = RecognitionEngine.Cloud,
                Cloud = new CloudRecognitionSettings($"http://127.0.0.1:{port}", protector.Protect("diag-key"), "diag-model"),
            },
            Streaming = new StreamingSettings(true, interval),
        };
        var network = new NetworkContext(() => new ProxyConfig(ProxyMode.None, "", "", null), ProxyEnvironment.Empty);
        var recognition = new SettingsRecognizer(() => settings, protector, network, new NoLocalRecognizer());
        var recorder = new WavStreamingRecorder(wavs, speed);
        var injector = new SegmentLogInjector(t0);
        ITextPolisher? polisher = polish ? new SlowFirstPolisher() : null;
        using var coordinator = new SessionCoordinator(recorder, recognition, injector, polisher, null, () => settings.Streaming);
        var idle = new ManualResetEventSlim();
        var recording = new ManualResetEventSlim();
        var flushingAt = -1L;
        coordinator.StateChanged += s =>
        {
            Console.WriteLine($"{Ts()} state={s.State} streaming={s.IsStreaming}");
            if (s.State == SessionState.Recording) recording.Set();
            if (s.State == SessionState.Flushing && flushingAt < 0) flushingAt = t0.ElapsedMilliseconds;
            if (s.State == SessionState.Idle) idle.Set();
        };
        coordinator.Noticed += n => Console.WriteLine($"{Ts()} notice:{n.Kind} {n.Message}");

        t0.Restart();
        coordinator.Toggle();
        if (!recording.Wait(5000)) { Console.WriteLine("RESULT=FAIL reason=not_recording"); return 1; }
        var totalSec = source.Length / (double)PcmAudio.SampleRate;
        var actionAt = cancelAt ?? stopAt ?? totalSec;
        var actionMs = (long)(actionAt / speed * 1000);
        while (t0.ElapsedMilliseconds < actionMs) Thread.Sleep(20);
        var fedAtAction = recorder.FedSamples;
        Console.WriteLine($"{Ts()} {(cancelAt is not null ? "cancel" : "stop")}_requested audio_fed={fedAtAction / (double)PcmAudio.SampleRate:0.0}s");
        var actionAtMs = t0.ElapsedMilliseconds;
        if (cancelAt is not null) coordinator.Cancel(); else coordinator.Toggle();
        var finished = idle.Wait(TimeSpan.FromSeconds(90));
        Thread.Sleep(cancelAt is not null ? 1500 : 200);

        var seen = injector.Segments;
        var ids = seen.Select(x => int.Parse(x.Text.Trim().Replace("区間", ""))).ToArray();
        var ordered = ids.SequenceEqual(ids.OrderBy(x => x)) && ids.Distinct().Count() == ids.Length;
        var expected = (int)Math.Ceiling(fedAtAction / (double)segSamples);
        var afterStop = seen.Count(x => x.AtMs >= actionAtMs);
        Console.WriteLine($"injections={seen.Count} order_ok={ordered} expected_segments={expected} requests_served={served} peak_concurrent_requests={peakInFlight}");
        Console.WriteLine($"injected_before_stop={seen.Count - afterStop} injected_after_stop={afterStop} separators={string.Join(",", seen.Select(x => x.Text.StartsWith(' ') ? "sp" : "-"))}");
        Console.WriteLine($"finished_idle={finished} audio_fed_at_action={fedAtAction / (double)PcmAudio.SampleRate:0.0}s total_audio={totalSec:0.0}s speed={speed}");
        listener.Stop();
        if (cancelAt is not null)
        {
            var late = seen.Count(x => x.AtMs > actionAtMs + 50);
            Console.WriteLine($"injected_after_cancel={late}");
            return finished && late == 0 ? 0 : 1;
        }
        return finished && ordered ? 0 : 1;
    }

    private sealed class NoLocalRecognizer : IRecognizer
    {
        public string? CheckReady() => "ローカル認識は使いません";
        public Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class SegmentLogInjector(Stopwatch clock) : ITextInjector
    {
        private readonly object _l = new();
        private readonly List<(long AtMs, string Text)> _seen = [];
        public IReadOnlyList<(long AtMs, string Text)> Segments { get { lock (_l) return _seen.ToArray(); } }

        public Task<InjectResult> InjectAsync(string text, CancellationToken ct) => throw new NotSupportedException("ストリーミングの区間投入だけを使う");
        public IInjectTarget? CaptureTarget() => new DiagTarget();

        public Task<InjectResult> InjectSegmentAsync(string text, IInjectTarget target, CancellationToken ct)
        {
            lock (_l) _seen.Add((clock.ElapsedMilliseconds, text));
            Console.WriteLine($"[{clock.ElapsedMilliseconds,7} ms] INJECT {(text.StartsWith(' ') ? "(sep)" : "")}{text.Trim()}");
            return Task.FromResult(new InjectResult(InjectOutcome.Success));
        }

        private sealed record DiagTarget : IInjectTarget;
    }

    /// <summary>整文のモック: 区間 1 の整文だけ遅らせ、後続が先に整文を終える（投入順は連番のままのはず）。</summary>
    private sealed class SlowFirstPolisher : ITextPolisher
    {
        public PolishSetup Prepare() => new PolishSetup.Ready(async (raw, ct) =>
        {
            await Task.Delay(raw.EndsWith("01") ? 2500 : 100, ct);
            return PolishResult.Success(raw);
        }, TimeSpan.FromSeconds(15));
    }
}
