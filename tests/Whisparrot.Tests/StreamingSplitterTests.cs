using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Settings;
using static Whisparrot.App.Windows.TextInjector;

namespace Whisparrot.Tests;

/// <summary>S-1: 固定秒数の区間分割・有界バッファ・連続リサンプラー・区間境界の空白補完。</summary>
public sealed class StreamingSplitterTests
{
    private const int Rate = PcmAudio.SampleRate;

    private static float[] Ramp(long from, int count) => Enumerable.Range(0, count).Select(i => (float)(from + i)).ToArray();

    private static List<AudioSegment> Feed(SegmentSplitter sp, long total, int frame)
    {
        var all = new List<AudioSegment>();
        for (long pos = 0; pos < total; pos += frame)
            all.AddRange(sp.Push(pos, Ramp(pos, (int)Math.Min(frame, total - pos))));
        return all;
    }

    [Fact]
    public void Cuts_at_the_target_length_with_sequence_from_one()
    {
        var sp = SegmentSplitter.ForSeconds(8);
        var segs = Feed(sp, 8L * Rate * 3, 1600);

        Assert.Equal([1, 2, 3], segs.Select(s => s.Sequence));
        Assert.All(segs, s => Assert.Equal(8 * Rate, s.Pcm.Samples.Length));
        Assert.Equal([0L, 8L * Rate, 16L * Rate], segs.Select(s => s.StartSample));
        Assert.Null(sp.Flush()); // ちょうど割り切れたときの末尾は空
    }

    [Fact]
    public void Result_does_not_depend_on_frame_size_and_samples_are_contiguous()
    {
        var total = 8L * Rate * 2 + 12345;
        foreach (var frame in new[] { 1, 160, 441, 1600, 7777, 128000 })
        {
            var sp = SegmentSplitter.ForSeconds(8);
            var segs = Feed(sp, total, frame);
            if (sp.Flush() is { } tail) segs.Add(tail);

            Assert.Equal(3, segs.Count);
            Assert.Equal(total, segs.Sum(s => (long)s.Pcm.Samples.Length));
            // 取りこぼし・重複なし: 連結すると元のランプに一致する。
            var joined = segs.SelectMany(s => s.Pcm.Samples).ToArray();
            for (var i = 0; i < joined.Length; i += 997) Assert.Equal((float)i, joined[i]);
            Assert.Equal((float)(total - 1), joined[^1]);
            Assert.Equal(segs.Select((_, i) => i + 1), segs.Select(s => s.Sequence));
        }
    }

    [Fact]
    public void Tail_shorter_than_any_minimum_is_still_emitted_once_on_flush()
    {
        var sp = SegmentSplitter.ForSeconds(8);
        Assert.Empty(sp.Push(0, Ramp(0, 800))); // 50 ms
        var tail = sp.Flush();

        Assert.NotNull(tail);
        Assert.Equal(1, tail!.Sequence);
        Assert.Equal(800, tail.Pcm.Samples.Length);
        Assert.Null(sp.Flush());                     // 二度は確定しない
        Assert.Empty(sp.Push(800, Ramp(800, 100)));  // Flush 後の入力は無視（終端の一意性）
        Assert.Equal(1, sp.SegmentCount);
    }

    [Fact]
    public void Empty_recording_yields_no_segment()
    {
        var sp = SegmentSplitter.ForSeconds(4);
        Assert.Null(sp.Flush());
        Assert.Equal(0, sp.SegmentCount);
    }

    [Fact]
    public void A_gap_closes_the_current_segment_and_restarts_after_it()
    {
        var sp = SegmentSplitter.ForSeconds(4);
        Assert.Empty(sp.Push(0, Ramp(0, 1000)));
        var cut = sp.Push(5000, Ramp(5000, 500)); // 1000..5000 が欠けた

        var first = Assert.Single(cut);
        Assert.Equal(1000, first.Pcm.Samples.Length);
        Assert.Equal(4000, sp.GapSamples);
        var tail = sp.Flush()!;
        Assert.Equal(2, tail.Sequence);
        Assert.Equal(5000, tail.StartSample); // 欠けをまたいで連結しない
        Assert.Equal(500, tail.Pcm.Samples.Length);
        Assert.Equal(5500, sp.Position);
    }

    [Fact]
    public void Overlapping_frames_are_trimmed_so_no_sample_is_counted_twice()
    {
        var sp = SegmentSplitter.ForSeconds(4);
        sp.Push(0, Ramp(0, 1000));
        sp.Push(500, Ramp(500, 1000)); // 500..1000 は既に取り込み済み
        sp.Push(0, Ramp(0, 100));      // 完全に過去
        var tail = sp.Flush()!;

        Assert.Equal(1500, tail.Pcm.Samples.Length);
        Assert.Equal((float)1499, tail.Pcm.Samples[^1]);
    }

    [Fact]
    public void Segment_end_equals_next_start_for_adjacent_segments()
    {
        var sp = SegmentSplitter.ForSeconds(4);
        var segs = Feed(sp, 4L * Rate * 2 + 10, 3200);
        segs.Add(sp.Flush()!);
        for (var i = 1; i < segs.Count; i++) Assert.Equal(segs[i - 1].EndSample, segs[i].StartSample);
    }

    [Fact]
    public void Splitter_rejects_a_non_positive_target() => Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentSplitter(0));

    // ── 区間境界の空白 ──

    [Theory]
    [InlineData('a', "bc", " ")]
    [InlineData('9', "x", " ")]
    [InlineData('a', "9", " ")]
    [InlineData('。', "abc", "")]
    [InlineData('a', "あ", "")]
    [InlineData('あ', "abc", "")]
    [InlineData('a', ".", "")]
    [InlineData('a', "", "")]
    public void Separator_only_between_ascii_alphanumerics(char prev, string next, string expected) =>
        Assert.Equal(expected, SegmentJoin.Separator(prev, next));

    [Fact]
    public void Separator_is_empty_for_the_first_segment() => Assert.Equal("", SegmentJoin.Separator(null, "abc"));

    // ── 有界バッファ ──

    [Fact]
    public void Frame_buffer_drops_when_full_counts_them_and_keeps_positions_unique()
    {
        var buf = new FrameBuffer(300);
        var results = Enumerable.Range(0, 5).Select(_ => buf.Offer(new float[100])).ToArray();

        Assert.Equal([true, true, true, false, false], results);
        Assert.Equal(200, buf.Dropped);
        Assert.Equal(500, buf.Position); // 省いた分も位置は進む
        // 読み手が一つ取ると空きができ、次のフレームの開始位置に欠けが現れる。
        Assert.True(buf.Reader.TryRead(out var f0));
        Assert.Equal(0, f0!.StartSample);
        Assert.True(buf.Offer(new float[100]));
        buf.Complete();
        var starts = new List<long>();
        while (buf.Reader.TryRead(out var f)) starts.Add(f.StartSample);
        Assert.Equal([100L, 200L, 500L], starts); // 300..500 の欠けが StartSample の不連続で分かる
        Assert.True(buf.Reader.Completion.IsCompleted);
        Assert.Equal(0, buf.Queued);
    }

    [Fact]
    public async Task Frame_buffer_limit_is_in_samples_not_frames_and_works_with_ReadAllAsync()
    {
        var buf = new FrameBuffer(2 * Rate); // 2 秒
        // 60 ms のフレームを 40 個（2.4 秒）。2 秒を超えた分は省かれる。
        var accepted = Enumerable.Range(0, 40).Count(_ => buf.Offer(new float[960]));
        Assert.Equal(2 * Rate / 960, accepted); // 33 個
        Assert.Equal((40 - accepted) * 960L, buf.Dropped);
        buf.Complete();
        var read = 0L;
        await foreach (var f in buf.Reader.ReadAllAsync()) read += f.Samples.Length;
        Assert.Equal(accepted * 960L, read);
        Assert.Equal(0, buf.Queued);
    }

    [Fact]
    public void Splitter_sees_the_dropped_range_as_a_gap()
    {
        var buf = new FrameBuffer(1000);
        buf.Offer(new float[1000]);
        buf.Offer(new float[1000]); // 省かれる
        Assert.True(buf.Reader.TryRead(out var a));
        buf.Offer(new float[1000]);
        buf.Complete();
        Assert.True(buf.Reader.TryRead(out var b));

        var sp = SegmentSplitter.ForSeconds(4);
        sp.Push(a!.StartSample, a.Samples);
        sp.Push(b!.StartSample, b.Samples);
        Assert.Equal(1000, sp.GapSamples);
    }

    // ── 連続リサンプラー ──

    [Fact]
    public void Resampler_passes_16k_through_unchanged()
    {
        var r = new StreamingResampler(Rate);
        var input = Ramp(0, 500);
        Assert.Equal(input, r.Process(input));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Resampler_keeps_state_across_frames_so_chunked_output_is_a_clean_continuous_wave(int sourceRate)
    {
        var seconds = 3;
        var input = Enumerable.Range(0, sourceRate * seconds).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / sourceRate) * 0.5f).ToArray();

        var whole = new StreamingResampler(sourceRate).Process(input);
        var chunked = new List<float>();
        var chunker = new StreamingResampler(sourceRate);
        var frame = sourceRate / 100; // 10 ms
        for (var i = 0; i < input.Length; i += frame) chunked.AddRange(chunker.Process(input.AsSpan(i, Math.Min(frame, input.Length - i))));

        // 長さは 16 kHz × 秒数に近く（遅延分だけ少ない）、フレーム分割に依存しない。
        Assert.InRange(chunked.Count, Rate * seconds - 400, Rate * seconds + 16);
        Assert.InRange(Math.Abs(chunked.Count - whole.Length), 0, 64);
        // フレーム境界でクリック・ずれが出ない: 440 Hz・振幅 0.5 の正弦波なら、隣り合うサンプルの差は最大でも 0.5·2π·440/16000 ≈ 0.087 で、RMS は 0.354。
        double maxStep = 0, sum = 0;
        for (var i = 200; i < chunked.Count; i++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(chunked[i] - chunked[i - 1]));
            sum += chunked[i] * chunked[i];
        }
        var rms = Math.Sqrt(sum / (chunked.Count - 200));
        Assert.True(maxStep < 0.095, $"max step {maxStep}");
        Assert.InRange(rms, 0.34, 0.37);
    }

    // ── 厳密な入力先確認（S-3）──

    private static PasteTarget Recorded(bool hosted = false) =>
        new(new IntPtr(100), new IntPtr(200), hosted ? new IntPtr(300) : new IntPtr(200), "Win", "Edit", "Edit", "notepad", hosted, Pid: 42);

    [Fact]
    public void Strict_match_requires_same_foreground_pid_focus_and_live_target()
    {
        var t = Recorded();
        Assert.Null(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(200), 42), targetAlive: true));
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(101), new IntPtr(200), 42), true));  // 別の前景
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(201), 42), true));  // 別のコントロール
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), IntPtr.Zero, 42), true));      // フォーカスなし
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(200), 43), true));  // PID 違い（ハンドル再利用）
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(200), 42), false)); // 宛先が消えた
    }

    [Fact]
    public void Strict_hosted_editor_accepts_focus_on_the_edit_itself_but_nothing_else()
    {
        var t = Recorded(hosted: true);
        Assert.Null(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(200), 42), true));
        Assert.Null(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(300), 42), true));
        Assert.NotNull(StrictMismatch(t, new FocusSnapshot(new IntPtr(100), new IntPtr(999), 42), true));
    }

    [Fact]
    public void Strict_injection_without_a_recorded_target_fails_without_touching_anything()
    {
        var r = new TextInjector().InjectStrictCore("x", null, CancellationToken.None, null);
        Assert.Equal(InjectOutcome.Failed, r.Outcome);
        Assert.False(r.EffectUnknown);
    }

    // ── 設定 ──

    [Fact]
    public void Streaming_defaults_are_off_and_eight_seconds()
    {
        Assert.False(AppSettings.Default.Streaming.Enabled);
        Assert.Equal(8, AppSettings.Default.Streaming.IntervalSeconds);
    }

    [Fact]
    public void Existing_config_without_streaming_loads_with_defaults_and_is_written_back_unchanged()
    {
        var text = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        Assert.DoesNotContain("streaming", text); // 既定のときは新しいキーを書かない
        var loaded = SettingsJson.Parse(text, DateTimeOffset.UtcNow);
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(StreamingSettings.Default, loaded.Settings.Streaming);
    }

    [Fact]
    public void Streaming_settings_round_trip()
    {
        var s = AppSettings.Default with { Streaming = new StreamingSettings(true, 12) };
        var loaded = SettingsJson.Parse(System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(s)), DateTimeOffset.UtcNow);
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(new StreamingSettings(true, 12), loaded.Settings.Streaming);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(20, true)]
    [InlineData(3, false)]
    [InlineData(21, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void Interval_must_be_an_integer_from_4_to_20(int seconds, bool valid)
    {
        var issues = SettingsValidator.Validate(AppSettings.Default with { Streaming = new StreamingSettings(true, seconds) }, DateTimeOffset.UtcNow);
        Assert.Equal(valid, !issues.Any(i => i.Path == "streaming.interval_seconds"));
    }

    [Fact]
    public void Non_integer_or_unknown_streaming_keys_are_rejected_on_load()
    {
        var baseText = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        string With(string streaming) => baseText.TrimEnd().TrimEnd('}') + ",\n  \"streaming\": " + streaming + "\n}\n";

        Assert.Equal(SettingsLoadStatus.Invalid, SettingsJson.Parse(With("{\"interval_seconds\": 8.5}"), DateTimeOffset.UtcNow).Status);
        Assert.Equal(SettingsLoadStatus.Invalid, SettingsJson.Parse(With("{\"interval_seconds\": 99}"), DateTimeOffset.UtcNow).Status);
        Assert.Equal(SettingsLoadStatus.Invalid, SettingsJson.Parse(With("{\"enabled\": true, \"bogus\": 1}"), DateTimeOffset.UtcNow).Status);
        var ok = SettingsJson.Parse(With("{\"enabled\": true}"), DateTimeOffset.UtcNow);
        Assert.Equal(new StreamingSettings(true, 8), ok.Settings.Streaming); // 間隔は既定
    }
}
