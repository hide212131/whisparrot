using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests;

/// <summary>クラウド文字起こし設計 §4.3 / §9.1（T24）: 設定画面のクラウド欄が使う、画面に依存しない判定。</summary>
public sealed class CloudConnectionFormTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Copy_takes_url_and_the_key_ciphertext_only()
    {
        var cipher = _protector.Protect("dummy-key-0123456789")!;
        var r = CloudConnectionForm.CopyFromCleanup(new CompatibleCleanupSettings("https://llm.example.com/v1", cipher, "polish-model"), _protector);
        Assert.True(r.Ok);
        Assert.Equal("https://llm.example.com/v1", r.BaseUrl);
        Assert.Equal(cipher, r.KeyCipher);
    }

    [Fact]
    public void Copy_of_a_source_without_a_key_yields_no_key()
    {
        var r = CloudConnectionForm.CopyFromCleanup(new CompatibleCleanupSettings("http://localhost:4000/v1", null, ""), _protector);
        Assert.True(r.Ok);
        Assert.Null(r.KeyCipher);
    }

    [Fact]
    public void Copy_is_refused_when_the_source_is_unset()
    {
        var r = CloudConnectionForm.CopyFromCleanup(new CompatibleCleanupSettings("", null, ""), _protector);
        Assert.False(r.Ok);
        Assert.Equal("", r.BaseUrl);
    }

    [Fact]
    public void Copy_is_refused_when_the_source_key_cannot_be_decrypted()
    {
        var broken = Convert.ToBase64String("not-a-dpapi-blob"u8.ToArray());
        var r = CloudConnectionForm.CopyFromCleanup(new CompatibleCleanupSettings("https://llm.example.com/v1", broken, "m"), _protector);
        Assert.False(r.Ok);
        Assert.Null(r.KeyCipher);
        Assert.Contains("復号できない", r.Error);
    }

    [Theory]
    [InlineData("https://transcription.example.com/v1", "https://transcription.example.com/v1/audio/transcriptions")]
    [InlineData("https://transcription.example.com/gateway/v1/", "https://transcription.example.com/gateway/v1/audio/transcriptions")]
    [InlineData("http://localhost:4000/v1", "http://localhost:4000/v1/audio/transcriptions")]
    public void Request_target_follows_the_design_examples(string input, string expected) =>
        Assert.Equal(expected, CloudConnectionForm.RequestTarget(input));

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com/v1")]
    [InlineData("https://user@example.com/v1")]
    [InlineData("https://example.com/v1?x=1")]
    [InlineData("example.com/v1")]
    public void Request_target_is_null_for_an_invalid_url(string input) => Assert.Null(CloudConnectionForm.RequestTarget(input));

    private RecognitionSettings Rec(string url, string? cipher, string model) =>
        AppSettings.Default.Recognition with { Cloud = new CloudRecognitionSettings(url, cipher, model) };

    [Fact]
    public void Not_ready_reason_names_the_first_missing_piece()
    {
        Assert.Contains("ベース URL が未設定", CloudConnectionForm.NotReadyReason(Rec("", null, ""), _protector));
        Assert.Contains("ベース URL が不正", CloudConnectionForm.NotReadyReason(Rec("http://example.com/v1", null, "m"), _protector));
        Assert.Contains("モデル名が未設定", CloudConnectionForm.NotReadyReason(Rec("https://example.com/v1", null, " "), _protector));
        Assert.Contains("API キーが未設定", CloudConnectionForm.NotReadyReason(Rec("https://example.com/v1", null, "m"), _protector));
        var broken = Convert.ToBase64String("not-a-dpapi-blob"u8.ToArray());
        Assert.Contains("復号できません", CloudConnectionForm.NotReadyReason(Rec("https://example.com/v1", broken, "m"), _protector));
    }

    [Fact]
    public void Not_ready_reason_is_null_when_complete_and_loopback_may_omit_the_key()
    {
        Assert.Null(CloudConnectionForm.NotReadyReason(Rec("https://example.com/v1", _protector.Protect("dummy-key-0123456789"), "whisper"), _protector));
        Assert.Null(CloudConnectionForm.NotReadyReason(Rec("http://127.0.0.1:4000/v1", null, "whisper"), _protector));
    }

    [Fact]
    public void Not_ready_reason_does_not_depend_on_the_engine_flag_and_never_leaks_the_key()
    {
        var key = "dummy-key-NOT-A-REAL-SECRET";
        var reason = CloudConnectionForm.NotReadyReason(Rec("https://example.com/v1", _protector.Protect(key), ""), _protector);
        Assert.NotNull(reason);
        Assert.DoesNotContain(key, reason);
    }
}

/// <summary>ピル表示の入力: スナップショットが、録音開始時に固定した認識先を進行中だけ運ぶ。</summary>
public sealed class SessionSnapshotEngineTests : IDisposable
{
    private readonly FakeRecorder _rec = new();
    private readonly FakeInjector _inj = new();
    private readonly SessionCoordinator _c;
    private readonly List<SessionSnapshot> _snaps = [];
    private RecognitionSetup _setup = new RecognitionSetup.Ready(RecognitionEngine.Cloud, (_, _) => Task.FromResult(new RecognitionResult("x", "cloud")), TimeSpan.FromSeconds(60));

    private sealed class Src(SessionSnapshotEngineTests t) : IRecognitionSource { public RecognitionSetup Prepare() => t._setup; }

    public SessionSnapshotEngineTests()
    {
        _c = new SessionCoordinator(_rec, new Src(this), _inj);
        _c.StateChanged += s => { lock (_snaps) _snaps.Add(s); };
    }

    public void Dispose() => _c.Dispose();

    [Fact]
    public async Task Cloud_engine_is_reported_while_recording_and_recognizing_and_cleared_at_idle()
    {
        _c.Toggle();
        await Wait.UntilAsync(() => _c.Snapshot.State == SessionState.Recording, "recording");
        _c.Toggle();
        await Wait.UntilAsync(() => _c.Snapshot.State == SessionState.Idle && _inj.Texts.Count == 1, "idle");
        lock (_snaps)
        {
            Assert.All(_snaps.Where(s => s.State != SessionState.Idle), s => Assert.Equal(RecognitionEngine.Cloud, s.Engine));
            Assert.Contains(_snaps, s => s.State == SessionState.Recognizing);
            Assert.Null(_snaps.Last().Engine);
        }
    }

    [Fact]
    public async Task Local_engine_is_reported_for_a_local_session()
    {
        _setup = new RecognitionSetup.Ready(RecognitionEngine.Local, (_, _) => Task.FromResult(new RecognitionResult("x", "cpu")), null);
        _c.Toggle();
        await Wait.UntilAsync(() => _c.Snapshot.State == SessionState.Recording, "recording");
        Assert.Equal(RecognitionEngine.Local, _c.Snapshot.Engine);
        _c.Cancel();
        await Wait.UntilAsync(() => _c.Snapshot.State == SessionState.Idle, "idle");
        Assert.Null(_c.Snapshot.Engine);
    }
}
