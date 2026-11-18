using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

/// <summary>設計 §3.2 / §5 / §10.1: クラウド認識の Coordinator 結線（認識の構えの固定・失敗の非投入・期限と取消の競合）。</summary>
public sealed class CoordinatorRecognitionSetupTests : IDisposable
{
    private readonly FakeRecorder _rec = new();
    private readonly FakeInjector _inj = new();
    private readonly FakeSource _src = new();
    private readonly FakePolishSource _pol = new();
    private readonly SessionCoordinator _c;
    private readonly List<SessionState> _states = [];
    private readonly List<SessionNotice> _notices = [];

    public CoordinatorRecognitionSetupTests()
    {
        _c = new SessionCoordinator(_rec, _src, _inj, _pol);
        _c.StateChanged += s => { lock (_states) _states.Add(s.State); };
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
    }

    public void Dispose() => _c.Dispose();

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    private async Task RecordAndStop()
    {
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle();
    }

    private static RecognitionSetup.Ready Cloud(Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>> f, TimeSpan? deadline = null) =>
        new(RecognitionEngine.Cloud, f, deadline ?? TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Cloud_success_goes_through_recognizing_and_injects_once_without_polish()
    {
        _src.Setup = Cloud((_, _) => Task.FromResult(new RecognitionResult("クラウドの結果", "cloud")));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["クラウドの結果"], _inj.Texts);
        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.Inserting, SessionState.Idle], _states);
    }

    [Fact]
    public async Task Cloud_text_flows_into_the_existing_polish_and_only_the_polished_text_is_injected()
    {
        string? seen = null;
        _src.Setup = Cloud((_, _) => Task.FromResult(new RecognitionResult("えーと原文", "cloud")));
        _pol.Setup = new PolishSetup.Ready((raw, _) => { seen = raw; return Task.FromResult(PolishResult.Success("整文済み")); }, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal("えーと原文", seen);
        Assert.Equal(["整文済み"], _inj.Texts);
        Assert.Contains(SessionState.Polishing, _states);
    }

    [Fact]
    public async Task Polish_failure_after_cloud_recognition_injects_the_cloud_text_exactly_once()
    {
        _src.Setup = Cloud((_, _) => Task.FromResult(new RecognitionResult("クラウド原文", "cloud")));
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(PolishFailure.Http, "HTTP 500")), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["クラウド原文"], _inj.Texts);
    }

    [Fact]
    public async Task Cloud_failure_notifies_with_the_fixed_message_and_injects_nothing_and_never_polishes()
    {
        _src.Setup = Cloud((_, _) => Task.FromException<RecognitionResult>(new RecognitionException("文字起こしできませんでした（認証エラー）。設定を確認してください")));
        _pol.Setup = new PolishSetup.Ready((_, _) => throw new InvalidOperationException("整文は呼ばれない"), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);

        Assert.Empty(_inj.Texts);
        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.Idle], _states);   // Failed（未投入あり）を経由しない
        Assert.False(_c.Snapshot.HasPendingText);
        var n = Assert.Single(_notices);
        Assert.Equal(NoticeKind.Error, n.Kind);
        Assert.Contains("認証エラー", n.Message);
        _c.RetryInsert();   // 音声や原文を再送・再投入する操作は何も起こさない
        await _c.FlushAsync();
        Assert.Empty(_inj.Texts);
        Assert.Equal(1, _src.RecognizeCalls);
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_generic_failure_without_its_message()
    {
        _src.Setup = Cloud((_, _) => Task.FromException<RecognitionResult>(new InvalidOperationException("secret-detail Bearer abc")));
        await RecordAndStop();
        await InState(SessionState.Idle);
        var n = Assert.Single(_notices);
        Assert.DoesNotContain("secret-detail", n.Message);
        Assert.Empty(_inj.Texts);
    }

    [Fact]
    public async Task An_empty_text_is_not_injected_and_is_reported_differently_from_an_error()
    {
        _src.Setup = Cloud((_, _) => Task.FromResult(new RecognitionResult("", "cloud")));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Empty(_inj.Texts);
        var n = Assert.Single(_notices);
        Assert.Equal(NoticeKind.Info, n.Kind);
        Assert.Equal("音声を認識できませんでした", n.Message);
    }

    [Fact]
    public async Task An_unconfigured_setup_does_not_record_or_recognize()
    {
        _src.Setup = new RecognitionSetup.Unconfigured("クラウド認識の API キーが未設定です");
        _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(0, _rec.StartCount);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Equal("クラウド認識の API キーが未設定です", Assert.Single(_notices).Message);
        Assert.Equal(0, _src.RecognizeCalls);
    }

    [Fact]
    public async Task A_throwing_source_is_treated_as_unconfigured()
    {
        _src.PrepareError = new InvalidOperationException("boom");
        _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(0, _rec.StartCount);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task The_setup_is_fixed_when_recording_starts()
    {
        var firstUsed = 0;
        _src.Setup = Cloud((_, _) => { firstUsed++; return Task.FromResult(new RecognitionResult("A", "cloud")); });
        _c.Toggle(); await InState(SessionState.Recording);
        // 録音中に設定が変わっても、このセッションは開始時の構えを使う。
        _src.Setup = Cloud((_, _) => Task.FromResult(new RecognitionResult("B", "cloud")));
        _c.Toggle();
        await InState(SessionState.Idle);
        Assert.Equal(["A"], _inj.Texts);
        Assert.Equal(1, firstUsed);
        Assert.Equal(1, _src.PrepareCalls);

        _c.Toggle(); await InState(SessionState.Recording);   // 次のセッションから新しい構え
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Equal(["A", "B"], _inj.Texts);
    }

    [Fact]
    public async Task Pressing_the_hotkey_while_recognizing_does_not_start_a_new_recording()
    {
        var tcs = new TaskCompletionSource<RecognitionResult>();
        _src.Setup = Cloud((_, _) => tcs.Task);
        await RecordAndStop();
        await InState(SessionState.Recognizing);
        _c.Toggle(); _c.Toggle(); _c.Stop();
        await _c.FlushAsync();
        Assert.Equal(1, _rec.StartCount);
        Assert.Equal(SessionState.Recognizing, _c.Snapshot.State);
        Assert.Equal(1, _src.PrepareCalls);

        tcs.SetResult(new RecognitionResult("done", "cloud"));
        await InState(SessionState.Idle);
        Assert.Equal(["done"], _inj.Texts);
    }

    [Fact]
    public async Task Cancel_while_recognizing_cancels_the_request_and_injects_nothing_even_if_it_completes_late()
    {
        var tcs = new TaskCompletionSource<RecognitionResult>();
        CancellationToken seen = default;
        _src.Setup = Cloud((_, ct) => { seen = ct; return tcs.Task; });   // 取消を無視して後で成功する認識
        await RecordAndStop();
        await InState(SessionState.Recognizing);
        _c.Cancel();
        await InState(SessionState.Idle);
        Assert.True(seen.IsCancellationRequested);

        tcs.SetResult(new RecognitionResult("遅れた成功", "cloud"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Empty(_inj.Texts);
        Assert.Empty(_notices);                    // 取消は失敗として通知しない
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
    }

    [Fact]
    public async Task The_deadline_ends_the_session_cancels_the_request_and_a_late_success_is_discarded()
    {
        var tcs = new TaskCompletionSource<RecognitionResult>();
        CancellationToken seen = default;
        _src.Setup = Cloud((_, ct) => { seen = ct; return tcs.Task; }, TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 250, 4000);
        Assert.True(seen.IsCancellationRequested);
        var n = Assert.Single(_notices);
        Assert.Contains("時間切れ", n.Message);
        Assert.Empty(_inj.Texts);

        tcs.SetResult(new RecognitionResult("期限後の成功", "cloud"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Empty(_inj.Texts);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task A_stale_late_completion_does_not_touch_the_next_session()
    {
        var old = new TaskCompletionSource<RecognitionResult>();
        _src.Setup = Cloud((_, _) => old.Task, TimeSpan.FromMilliseconds(200));
        await RecordAndStop();
        await InState(SessionState.Idle);

        var next = new TaskCompletionSource<RecognitionResult>();
        _src.Setup = Cloud((_, _) => next.Task);
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);

        old.SetResult(new RecognitionResult("古い完了", "cloud"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Equal(SessionState.Recognizing, _c.Snapshot.State);
        Assert.Empty(_inj.Texts);

        next.SetResult(new RecognitionResult("新しい完了", "cloud"));
        await InState(SessionState.Idle);
        Assert.Equal(["新しい完了"], _inj.Texts);
    }

    [Fact]
    public async Task A_result_that_arrives_just_before_the_deadline_is_accepted_exactly_once()
    {
        _src.Setup = Cloud(async (_, _) => { await Task.Delay(50); return new RecognitionResult("間に合った", "cloud"); }, TimeSpan.FromSeconds(5));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["間に合った"], _inj.Texts);
        await Task.Delay(100);
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task The_recognizer_overload_still_behaves_as_before_for_local_recognition()
    {
        var stt = new FakeRecognizer { NotReady = "モデルがありません" };
        using var inj = new CountingInjector();
        using var c = new SessionCoordinator(_rec, stt, inj);
        c.Toggle();
        await c.FlushAsync();
        Assert.Equal(0, _rec.StartCount);
        Assert.Equal(0, stt.Calls);
    }

    private sealed class CountingInjector : ITextInjector, IDisposable
    {
        public Task<InjectResult> InjectAsync(string text, CancellationToken ct) => Task.FromResult(new InjectResult(InjectOutcome.Success));
        public void Dispose() { }
    }

    private sealed class FakeSource : IRecognitionSource
    {
        public RecognitionSetup Setup { get; set; } = new RecognitionSetup.Unconfigured("未設定");
        public Exception? PrepareError { get; set; }
        public int PrepareCalls { get; private set; }
        public int RecognizeCalls { get; private set; }

        public RecognitionSetup Prepare()
        {
            PrepareCalls++;
            if (PrepareError is not null) throw PrepareError;
            if (Setup is RecognitionSetup.Ready r)
                return r with { RecognizeAsync = (req, ct) => { RecognizeCalls++; return r.RecognizeAsync(req, ct); } };
            return Setup;
        }
    }

    private sealed class FakePolishSource : ITextPolisher
    {
        public PolishSetup Setup { get; set; } = new PolishSetup.Off();
        public PolishSetup Prepare() => Setup;
    }
}

/// <summary>SettingsRecognizer + 実 CloudTranscriber + 実 NetworkContext + SessionCoordinator を、ループバックのモックへの実 HTTP で通す結合試験。</summary>
public sealed class SettingsRecognizerPipelineTests : IDisposable
{
    private const string DummyKey = "dummy-key-NOT-A-REAL-SECRET-0123456789";

    private readonly DpapiSecretProtector _protector = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _local = new() { Result = new RecognitionResult("ローカルの結果", "cpu") };
    private readonly FakeInjector _inj = new();
    private AppSettings _settings = AppSettings.Default;
    private SessionCoordinator? _c;
    private readonly List<SessionNotice> _notices = [];

    public void Dispose()
    {
        _c?.Dispose();
        foreach (var d in _disposables) d.Dispose();
    }

    private TestHttpServer Server(Func<TestRequest, TestResponse>? h = null)
    {
        var s = new TestHttpServer(h ?? (_ => TestHttpServer.Json(200, new { text = "クラウドの結果" })));
        _disposables.Add(s);
        return s;
    }

    private SessionCoordinator Coordinator(ITextPolisher? polisher = null, TimeSpan? deadline = null)
    {
        var net = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Empty);
        var src = new SettingsRecognizer(() => _settings, _protector, net, _local, deadline);
        _c = new SessionCoordinator(_rec, src, _inj, polisher);
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
        return _c;
    }

    private void UseCloud(string url, string? plainKey = DummyKey, string model = "my-whisper") =>
        _settings = _settings with
        {
            Recognition = _settings.Recognition with
            {
                Engine = RecognitionEngine.Cloud,
                Cloud = new(url, _protector.Protect(plainKey), model),
            },
        };

    private static async Task Until(SessionCoordinator c, SessionState s) =>
        await Wait.UntilAsync(() => c.Snapshot.State == s, $"state {s}");

    private async Task Dictate(SessionCoordinator c)
    {
        c.Toggle(); await Until(c, SessionState.Recording);
        c.Toggle(); await Until(c, SessionState.Idle);
    }

    [Fact]
    public async Task Cloud_works_without_any_local_model_and_never_touches_the_local_recognizer()
    {
        var server = Server();
        UseCloud(server.BaseUrl);
        _local.NotReady = "モデルが未取得です";   // ローカルモデルなし
        await Dictate(Coordinator());

        Assert.Equal(["クラウドの結果"], _inj.Texts);
        Assert.Equal(0, _local.Calls);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/v1/audio/transcriptions", req.Target);
        Assert.Equal("Bearer " + DummyKey, req.Headers["Authorization"]);
    }

    [Fact]
    public async Task Local_selection_sends_no_cloud_request_and_keeps_the_local_readiness_check()
    {
        var server = Server();
        UseCloud(server.BaseUrl);
        _settings = _settings with { Recognition = _settings.Recognition with { Engine = RecognitionEngine.Local } };   // クラウド設定を残して local
        await Dictate(Coordinator());
        Assert.Equal(["ローカルの結果"], _inj.Texts);
        Assert.Equal(1, _local.Calls);
        Assert.Empty(server.Requests);

        _local.NotReady = "モデルがありません";
        _c!.Toggle();
        await _c.FlushAsync();
        Assert.Equal(1, _rec.StartCount);   // 準備不能のローカルは録音しない（従来どおり）
        Assert.Equal("モデルがありません", _notices.Last().Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_key_that_cannot_be_decrypted_blocks_recording_and_sends_nothing_and_keeps_the_ciphertext()
    {
        var server = Server();
        UseCloud(server.BaseUrl);
        var broken = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        _settings = _settings with { Recognition = _settings.Recognition with { Cloud = _settings.Recognition.Cloud with { ApiKeyDpapi = broken } } };
        var c = Coordinator();
        c.Toggle();
        await c.FlushAsync();
        Assert.Equal(0, _rec.StartCount);
        Assert.Empty(server.Requests);
        Assert.Equal(0, _local.Calls);
        Assert.Contains("復号", _notices.Single().Message);
        Assert.Equal(broken, _settings.Recognition.Cloud.ApiKeyDpapi);
    }

    [Fact]
    public async Task An_unconfigured_cloud_never_falls_back_to_the_local_recognizer()
    {
        _settings = _settings with { Recognition = _settings.Recognition with { Engine = RecognitionEngine.Cloud } };
        var c = Coordinator();
        c.Toggle();
        await c.FlushAsync();
        Assert.Equal(0, _rec.StartCount);
        Assert.Equal(0, _local.Calls);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task Cloud_failure_does_not_fall_back_to_local_and_injects_nothing()
    {
        var server = Server(_ => TestHttpServer.Json(500, new { error = "x" }));
        UseCloud(server.BaseUrl);
        await Dictate(Coordinator());
        Assert.Empty(_inj.Texts);
        Assert.Equal(0, _local.Calls);
        Assert.Equal(1, server.Requests.Count);       // アプリによる再送なし
        Assert.Contains("HTTP 500", _notices.Single().Message);
        Assert.DoesNotContain(DummyKey, _notices.Single().Message);
    }

    [Fact]
    public async Task Cloud_text_goes_through_the_polisher_and_a_polish_failure_injects_the_raw_cloud_text_once()
    {
        var server = Server();
        UseCloud(server.BaseUrl);
        var pol = new StubPolisher(new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(PolishFailure.Http, "HTTP 500")), TimeSpan.FromSeconds(15)));
        await Dictate(Coordinator(pol));
        Assert.Equal(["クラウドの結果"], _inj.Texts);
        Assert.Equal(1, server.Requests.Count);       // 整文要求を認識先へ送っていない
    }

    [Fact]
    public async Task The_destination_is_fixed_when_recording_starts()
    {
        var first = Server();
        var second = Server(_ => TestHttpServer.Json(200, new { text = "別の宛先" }));
        UseCloud(first.BaseUrl);
        var c = Coordinator();
        c.Toggle(); await Until(c, SessionState.Recording);
        UseCloud(second.BaseUrl);                      // 録音中に送信先を変更
        c.Toggle(); await Until(c, SessionState.Idle);
        Assert.Single(first.Requests);
        Assert.Empty(second.Requests);
        Assert.Equal(["クラウドの結果"], _inj.Texts);

        await Dictate(c);                              // 次のセッションから新しい宛先
        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task A_stalled_server_ends_the_session_at_the_deadline_with_no_injection_and_the_next_session_works()
    {
        var release = new ManualResetEventSlim();
        _disposables.Add(new Releaser(release));
        var calls = 0;
        var server = Server(_ => { if (Interlocked.Increment(ref calls) == 1) release.Wait(20000); return TestHttpServer.Json(200, new { text = "二回目" }); });
        UseCloud(server.BaseUrl);
        var c = Coordinator(deadline: TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Dictate(c);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.4, 5);
        Assert.Empty(_inj.Texts);
        Assert.Contains("時間切れ", _notices.Single().Message);
        release.Set();

        await Dictate(c);
        Assert.Equal(["二回目"], _inj.Texts);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Cancel_while_the_request_is_in_flight_injects_nothing_and_the_next_session_succeeds()
    {
        var release = new ManualResetEventSlim();
        _disposables.Add(new Releaser(release));
        var calls = 0;
        var server = Server(_ => { if (Interlocked.Increment(ref calls) == 1) release.Wait(20000); return TestHttpServer.Json(200, new { text = "次のセッション" }); });
        UseCloud(server.BaseUrl);
        var c = Coordinator();
        c.Toggle(); await Until(c, SessionState.Recording);
        c.Toggle(); await Until(c, SessionState.Recognizing);
        await Wait.UntilAsync(() => server.Requests.Count == 1, "request arrived");
        c.Cancel();
        await Until(c, SessionState.Idle);
        release.Set();
        await Task.Delay(150);
        Assert.Empty(_inj.Texts);
        Assert.Empty(_notices);

        await Dictate(c);
        Assert.Equal(["次のセッション"], _inj.Texts);
    }

    private sealed class Releaser(ManualResetEventSlim e) : IDisposable { public void Dispose() => e.Set(); }

    private sealed class StubPolisher(PolishSetup setup) : ITextPolisher
    {
        public PolishSetup Prepare() => setup;
    }
}
