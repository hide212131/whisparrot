using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Whisparrot.Core;
using Whisparrot.Core.FileInput;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;

namespace Whisparrot.Tests;

public sealed class FileInputTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("whisparrot-fi-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 後始末の失敗は無視 */ }
    }

    // ---- 引数 ----

    [Fact]
    public void Options_parse_input_out_and_no_polish()
    {
        var o = TranscribeOptions.Parse(["a.wav", "--out", "o", "--no-polish"], out var error);
        Assert.Null(error);
        Assert.Equal(new TranscribeOptions("a.wav", "o", true), o);
        Assert.Equal(new TranscribeOptions("a.m4a", null, false), TranscribeOptions.Parse(["a.m4a"], out _));
    }

    [Theory]
    [InlineData]
    [InlineData("a.wav", "b.wav")]
    [InlineData("a.wav", "--out")]
    [InlineData("a.wav", "--out", "--no-polish")]
    [InlineData("a.wav", "--bogus")]
    public void Options_reject_bad_arguments_with_a_reason(params string[] args)
    {
        Assert.Null(TranscribeOptions.Parse(args, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---- 音声の読み込み ----

    private string WriteWav(string name, WaveFormat format, int frames, Func<int, int, double> sample)
    {
        var path = Path.Combine(_dir, name);
        using var w = new WaveFileWriter(path, format);
        var f = new float[frames * format.Channels];
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < format.Channels; c++) f[i * format.Channels + c] = (float)sample(i, c);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) w.WriteSamples(f, 0, f.Length);
        else
            foreach (var v in f)
                w.WriteSample(v);
        return path;
    }

    [Fact]
    public void Wav_16k_mono_is_read_as_is()
    {
        var path = WriteWav("a.wav", new WaveFormat(16000, 16, 1), 8000, (i, _) => 0.5);
        var pcm = AudioFileDecoder.Read(path);
        Assert.Equal(8000, pcm.Samples.Length);
        Assert.Equal(0.5f, pcm.Samples[100], 3);
    }

    [Fact]
    public void Wav_44k_stereo_becomes_16k_mono_with_the_same_duration()
    {
        // 左 0.5・右 -0.5 は平均で 0。左右とも 0.4 なら 0.4。
        var silent = WriteWav("s.wav", new WaveFormat(44100, 16, 2), 44100, (i, c) => c == 0 ? 0.5 : -0.5);
        var s = AudioFileDecoder.Read(silent);
        Assert.InRange(s.Duration.TotalSeconds, 0.99, 1.01);
        Assert.All(s.Samples.Skip(200).Take(1000), v => Assert.InRange(v, -0.01f, 0.01f));

        var loud = WriteWav("l.wav", new WaveFormat(44100, 16, 2), 44100, (i, c) => 0.4);
        var l = AudioFileDecoder.Read(loud);
        Assert.InRange(l.Duration.TotalSeconds, 0.99, 1.01);
        Assert.InRange(l.Samples[8000], 0.38f, 0.42f);
    }

    [Fact]
    public void Wav_float_48k_is_converted()
    {
        var path = WriteWav("f.wav", WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), 48000, (i, _) => 0.25);
        var pcm = AudioFileDecoder.Read(path);
        Assert.InRange(pcm.Duration.TotalSeconds, 0.99, 1.01);
        Assert.InRange(pcm.Samples[8000], 0.24f, 0.26f);
    }

    [Fact]
    public void Downmix_averages_channels()
    {
        Assert.Equal([0.5f, 0f], AudioFileDecoder.Downmix([1f, 0f, 0.5f, -0.5f], 2));
        Assert.Equal([2f / 3f], AudioFileDecoder.Downmix([1f, 1f, 0f], 3));
    }

    [Fact]
    public void Unsupported_extension_and_missing_file_are_rejected()
    {
        var mp3 = Path.Combine(_dir, "a.mp3");
        File.WriteAllBytes(mp3, [1, 2, 3]);
        var ex = Assert.Throws<UnsupportedAudioException>(() => AudioFileDecoder.Read(mp3));
        Assert.Contains(".mp3", ex.Message);
        Assert.Throws<FileNotFoundException>(() => AudioFileDecoder.Read(Path.Combine(_dir, "none.wav")));
    }

    [Fact]
    public void A_broken_wav_is_rejected_without_leaking_the_exception_type_up()
    {
        var path = Path.Combine(_dir, "broken.wav");
        File.WriteAllText(path, "not a wav at all");
        Assert.Throws<UnsupportedAudioException>(() => AudioFileDecoder.Read(path));
    }

    [Fact]
    public void M4a_is_decoded_through_media_foundation()
    {
        MediaFoundationApi.Startup();
        var wav = WriteWav("src.wav", new WaveFormat(44100, 16, 2), 44100 * 2, (i, c) => 0.3 * Math.Sin(2 * Math.PI * 440 * i / 44100));
        var m4a = Path.Combine(_dir, "t.m4a");
        using (var reader = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToAac(reader, m4a);
        var pcm = AudioFileDecoder.Read(m4a);
        Assert.InRange(pcm.Duration.TotalSeconds, 1.9, 2.2);
        Assert.Contains(pcm.Samples, v => Math.Abs(v) > 0.1f);
    }

    // ---- パイプライン ----

    private sealed class Polisher(PolishSetup setup) : ITextPolisher
    {
        public int Prepared { get; private set; }
        public PolishSetup Prepare() { Prepared++; return setup; }
    }

    private static PolishSetup.Ready PolishOk(string text) => new((_, _) => Task.FromResult(PolishResult.Success(text)), TimeSpan.FromSeconds(5));

    private static readonly PcmAudio OneSecond = new(new float[16000]);

    [Fact]
    public async Task Recognizes_then_polishes_through_the_same_setups_as_live_recording()
    {
        var rec = new FakeRecognizer { Result = new RecognitionResult("  えーと こんにちは  ", "cpu") };
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(rec), new Polisher(PolishOk(" こんにちは。 ")), false, default);
        Assert.Equal(TranscribeStatus.Ok, run.Status);
        Assert.Equal("えーと こんにちは", run.RawText);
        Assert.Equal("こんにちは。", run.PolishText);
        Assert.Equal(PolishOutcome.Ok, run.Polish);
        Assert.Equal("cpu", run.Backend);
        Assert.Equal(RecognitionEngine.Local, run.Engine);
        Assert.NotNull(run.RecognizeTime);
        Assert.NotNull(run.PolishTime);
    }

    [Fact]
    public async Task No_polish_skips_the_polisher_entirely()
    {
        var polisher = new Polisher(PolishOk("x"));
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), polisher, true, default);
        Assert.Equal(TranscribeStatus.Ok, run.Status);
        Assert.Equal(PolishOutcome.Skipped, run.Polish);
        Assert.Null(run.PolishText);
        Assert.Equal(0, polisher.Prepared);
    }

    [Fact]
    public async Task Polish_off_in_settings_is_skipped_not_failed()
    {
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), new Polisher(new PolishSetup.Off()), false, default);
        Assert.Equal(TranscribeStatus.Ok, run.Status);
        Assert.Equal(PolishOutcome.Skipped, run.Polish);
    }

    [Fact]
    public async Task A_recognizer_that_is_not_ready_fails_with_its_reason_and_never_recognizes()
    {
        var rec = new FakeRecognizer { NotReady = "モデルがありません" };
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(rec), null, false, default);
        Assert.Equal(TranscribeStatus.RecognitionFailed, run.Status);
        Assert.Equal("モデルがありません", run.Error);
        Assert.Equal(0, rec.Calls);
    }

    [Fact]
    public async Task Recognition_error_and_empty_text_are_recognition_failures()
    {
        var failing = new FakeRecognizer { Error = new RecognitionException("ワーカーが落ちました") };
        var a = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(failing), null, false, default);
        Assert.Equal((TranscribeStatus.RecognitionFailed, "ワーカーが落ちました"), (a.Status, a.Error));

        var empty = new FakeRecognizer { Result = new RecognitionResult("  ", "cpu") };
        var b = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(empty), null, false, default);
        Assert.Equal(TranscribeStatus.RecognitionFailed, b.Status);
        Assert.Null(b.RawText);
    }

    [Fact]
    public async Task Polish_failure_keeps_the_raw_text_and_reports_the_reason()
    {
        var failing = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(PolishFailure.Auth)), TimeSpan.FromSeconds(5));
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), new Polisher(failing), false, default);
        Assert.Equal(TranscribeStatus.PolishFailed, run.Status);
        Assert.Equal("こんにちは", run.RawText);
        Assert.Null(run.PolishText);
        Assert.Contains("認証エラー", run.Error);
    }

    [Fact]
    public async Task Unconfigured_polish_is_a_failure_not_a_silent_raw_fallback()
    {
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), new Polisher(new PolishSetup.Unconfigured("API キーが未設定です")), false, default);
        Assert.Equal(TranscribeStatus.PolishFailed, run.Status);
        Assert.Contains("API キーが未設定です", run.Error);
        Assert.Equal("こんにちは", run.RawText);
    }

    [Fact]
    public async Task A_polisher_that_ignores_its_timeout_is_cut_off()
    {
        var hang = new PolishSetup.Ready(async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(30)); return PolishResult.Success("late"); }, TimeSpan.FromMilliseconds(100));
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), new Polisher(hang), false, default);
        Assert.Equal(TranscribeStatus.PolishFailed, run.Status);
        Assert.Contains("時間切れ", run.Error);
    }

    [Fact]
    public async Task A_cloud_recognition_deadline_ends_a_hanging_request()
    {
        var source = new TestSource(new RecognitionSetup.Ready(RecognitionEngine.Cloud,
            async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(30)); return new RecognitionResult("late", "cloud"); }, TimeSpan.FromMilliseconds(100)));
        var run = await FileTranscriber.RunAsync(OneSecond, source, null, false, default);
        Assert.Equal(TranscribeStatus.RecognitionFailed, run.Status);
        Assert.Equal(RecognitionEngine.Cloud, run.Engine);
        Assert.Contains("時間切れ", run.Error);
    }

    private sealed class TestSource(RecognitionSetup setup) : IRecognitionSource
    {
        public RecognitionSetup Prepare() => setup;
    }

    // ---- 出力 ----

    private static TranscribeRecord Record(AppSettings? s, bool polish = true) =>
        new("v0.2.0", new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(9)), "part1.wav", "wav",
            TimeSpan.FromSeconds(65.4), TimeSpan.FromMilliseconds(30), s, polish, TimeSpan.FromMilliseconds(4321));

    [Fact]
    public async Task Writes_raw_polished_and_result_json_with_the_recorded_conditions()
    {
        var settings = AppSettings.Default with
        {
            Cleanup = AppSettings.Default.Cleanup with { Provider = CleanupProvider.Compatible, Compatible = new CompatibleCleanupSettings("https://example.invalid/v1", "SECRET-KEY", "polish-model") },
        };
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), new Polisher(PolishOk("整文後")), false, default);
        var written = TranscribeOutput.Write(Path.Combine(_dir, "out", "nested"), "part1", Record(settings), run);

        Assert.Equal(3, written.Count);
        var dir = Path.Combine(_dir, "out", "nested");
        Assert.Equal("こんにちは\n", File.ReadAllText(Path.Combine(dir, "part1.raw.txt")));
        Assert.Equal("整文後\n", File.ReadAllText(Path.Combine(dir, "part1.polished.txt")));

        var json = File.ReadAllText(Path.Combine(dir, "part1.result.json"));
        Assert.DoesNotContain("SECRET-KEY", json);
        Assert.DoesNotContain("example.invalid", json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("v0.2.0", root.GetProperty("app_version").GetString());
        Assert.StartsWith("2026-11-23T09:00:00", root.GetProperty("started_at").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(65.4, root.GetProperty("input").GetProperty("duration_seconds").GetDouble());
        var r = root.GetProperty("recognition");
        Assert.Equal("local", r.GetProperty("engine").GetString());
        Assert.Equal("small", r.GetProperty("model").GetString());
        Assert.Equal("cpu", r.GetProperty("backend").GetString());
        var p = root.GetProperty("polish");
        Assert.Equal("compatible", p.GetProperty("provider").GetString());
        Assert.Equal("polish-model", p.GetProperty("model").GetString());
        Assert.Equal("ok", p.GetProperty("result").GetString());
        var t = root.GetProperty("timings_ms");
        Assert.Equal(30, t.GetProperty("decode").GetDouble());
        Assert.Equal(4321, t.GetProperty("total").GetDouble());
        Assert.True(t.GetProperty("recognize").GetDouble() >= 0);
    }

    [Fact]
    public async Task No_polish_writes_no_polished_file()
    {
        var run = await FileTranscriber.RunAsync(OneSecond, new FixedRecognitionSource(new FakeRecognizer()), null, true, default);
        TranscribeOutput.Write(_dir, "a", Record(AppSettings.Default, polish: false), run);
        Assert.True(File.Exists(Path.Combine(_dir, "a.raw.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "a.polished.txt")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "a.result.json")));
        Assert.False(doc.RootElement.GetProperty("polish").GetProperty("requested").GetBoolean());
        Assert.Equal("skipped", doc.RootElement.GetProperty("polish").GetProperty("result").GetString());
    }

    [Fact]
    public void A_failed_run_still_leaves_result_json_with_the_reason()
    {
        var run = new TranscribeRun(TranscribeStatus.RecognitionFailed, "モデルが未取得です", null, null, null, null, PolishOutcome.Skipped, null, null);
        var written = TranscribeOutput.Write(_dir, "bad", Record(null) with { AudioDuration = null, DecodeTime = null }, run);
        Assert.Equal([Path.Combine(_dir, "bad.result.json")], written);
        using var doc = JsonDocument.Parse(File.ReadAllText(written[0]));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("モデルが未取得です", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("input").GetProperty("duration_seconds").ValueKind);
    }

    [Fact]
    public void Stem_drops_only_the_extension()
    {
        Assert.Equal("part1", TranscribeOutput.StemOf(@"C:\x\part1.wav"));
        Assert.Equal("a.b", TranscribeOutput.StemOf("a.b.m4a"));
    }
}
