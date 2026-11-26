using System.Threading.Channels;

namespace Whisparrot.Core;

/// <summary>
/// 連続録音のフレーム。16 kHz mono float。<paramref name="StartSample"/> は録音開始からのサンプル位置（受け入れを省いた箇所も数える）。
/// 前のフレームの終端と <see cref="StartSample"/> が一致しなければ、その間の音声は欠けている。
/// </summary>
public sealed record PcmFrame(long StartSample, float[] Samples);

/// <summary>
/// 録音を続けたまま 16 kHz の連続 PCM を返す録音（設計 §2・§3）。マイクは区間ごとに開閉しない。
/// <see cref="IRecorder.Cancel"/> と <see cref="IRecorder.Failed"/> は従来どおり使う。どの終了経路でも返したチャンネルは完了する。
/// </summary>
public interface IStreamingRecorder : IRecorder
{
    /// <summary>連続取り込みを開始する。<see cref="IRecorder.Start"/> の代わりに呼ぶ。フレームは有界バッファ（約 2 秒）を経由し、あふれたら新規の受け入れを省いて数える。</summary>
    ChannelReader<PcmFrame> StartStreaming();

    /// <summary>新規取り込みを終了する。末尾の余韻（既存の回収時間）を取り込んだあと、チャンネルを完了してデバイスを解放する。</summary>
    Task StopStreamingAsync();

    /// <summary>バッファがあふれて受け入れを省いたサンプル数（開始からの累計）。</summary>
    long DroppedSamples { get; }
}

/// <summary>確定した音声区間。<paramref name="Sequence"/> は 1 から。<paramref name="StartSample"/> は録音開始からのサンプル位置。</summary>
public sealed record AudioSegment(int Sequence, long StartSample, PcmAudio Pcm)
{
    public long EndSample => StartSample + Pcm.Samples.Length;
}

/// <summary>
/// 固定秒数の区間分割（設計 §2、最初のスライス）。16 kHz のサンプル位置だけで切るので、フレームの大きさに結果が依存しない。
/// 区間は目標長ちょうどで確定し、停止時の末尾は <see cref="Flush"/> で（短くても）一度だけ確定する。連番は 1 から連続し、重複しない。
/// フレームの途切れ（受け入れを省いた箇所）では、そこまでを一区間として確定し、続きを新しい区間の起点にする（音声をまたいで連結しない）。
/// </summary>
public sealed class SegmentSplitter
{
    private readonly int _target;
    private readonly List<float> _buffer = [];
    private long _bufferStart;
    private long _expected;
    private int _sequence;
    private bool _flushed;

    public SegmentSplitter(int targetSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetSamples, 1);
        _target = targetSamples;
    }

    public static SegmentSplitter ForSeconds(int seconds) => new(checked(seconds * PcmAudio.SampleRate));

    /// <summary>これまでに取り込んだ録音の終端位置（途切れを含む）。</summary>
    public long Position => _expected;

    /// <summary>途切れで失われたサンプル数の累計。</summary>
    public long GapSamples { get; private set; }

    /// <summary>確定済みの区間数。</summary>
    public int SegmentCount => _sequence;

    /// <summary>フレームを取り込み、確定した区間を順に返す。既に取り込んだ位置と重なる分は捨てる。<see cref="Flush"/> の後は何もしない。</summary>
    public IReadOnlyList<AudioSegment> Push(long startSample, ReadOnlySpan<float> samples)
    {
        var done = new List<AudioSegment>();
        if (_flushed || samples.IsEmpty) return done;

        if (startSample < _expected)
        {
            var skip = _expected - startSample;
            if (skip >= samples.Length) return done;
            samples = samples[(int)skip..];
            startSample = _expected;
        }
        else if (startSample > _expected)
        {
            GapSamples += startSample - _expected;
            if (_buffer.Count > 0) done.Add(Cut(_buffer.Count));
            _expected = startSample;
        }

        if (_buffer.Count == 0) _bufferStart = startSample;
        while (!samples.IsEmpty)
        {
            var take = Math.Min(_target - _buffer.Count, samples.Length);
            _buffer.AddRange(samples[..take].ToArray());
            samples = samples[take..];
            _expected += take;
            if (_buffer.Count == _target) done.Add(Cut(_target));
        }
        return done;
    }

    /// <summary>停止時の末尾。残りがあれば最小長未満でも一度だけ確定して返す。以後の <see cref="Push"/> は無視する。</summary>
    public AudioSegment? Flush()
    {
        if (_flushed) return null;
        _flushed = true;
        return _buffer.Count == 0 ? null : Cut(_buffer.Count);
    }

    private AudioSegment Cut(int count)
    {
        var samples = _buffer.GetRange(0, count).ToArray();
        _buffer.RemoveRange(0, count);
        var segment = new AudioSegment(++_sequence, _bufferStart, new PcmAudio(samples));
        _bufferStart += count;
        return segment;
    }
}

/// <summary>区間の出力境界（設計 §4.2）。区間ごとに改行は入れず、連続する英数字の境界にだけ空白を 1 個補う。</summary>
public static class SegmentJoin
{
    /// <summary>直前に入力した本文の末尾の文字と、これから入れる本文の先頭が、ともに ASCII の英数字なら半角空白を返す。日本語の境界は何も足さない。</summary>
    public static string Separator(char? previousLast, string next) =>
        previousLast is { } p && next.Length > 0 && char.IsAsciiLetterOrDigit(p) && char.IsAsciiLetterOrDigit(next[0]) ? " " : "";
}

/// <summary>
/// ストリーミングの並行度・期限・上限（設計 §3 / §5 / §7 の初期値）。既定値が設計値で、テストだけが短い値に差し替える。
/// </summary>
public sealed record StreamingLimits
{
    /// <summary>区間確定から認識完了まで（待ち行列・送信を含む）。</summary>
    public TimeSpan RecognitionDeadline { get; init; } = TimeSpan.FromSeconds(24);

    /// <summary>区間確定から整文を待てる上限。越えたら原文を出力候補にする。整文の開始からの上限は <c>PolishSetup.Ready.Timeout</c>。</summary>
    public TimeSpan PolishCutoff { get; init; } = TimeSpan.FromSeconds(32);

    /// <summary>区間確定から、出力枠を待っている未送出の区間を打ち切るまで。</summary>
    public TimeSpan OutputWait { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>マイク解放から、残りの認識・整文を打ち切るまで。</summary>
    public TimeSpan Flush { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>フラッシュ期限の後、原文または整文がある区間を順に入力できる時間。</summary>
    public TimeSpan SendWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>1 回の録音の上限。</summary>
    public TimeSpan MaxRecording { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>期限を点検する間隔。</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>クラウド認識の同時実行数。</summary>
    public int CloudRecognitionConcurrency { get; init; } = 2;

    /// <summary>整文の同時実行数。</summary>
    public int PolishConcurrency { get; init; } = 2;

    /// <summary>区間確定から出力の終端までの保持上限。超えて新たに切れた区間は欠落として進む。</summary>
    public int MaxBufferedSegments { get; init; } = 8;

    /// <summary>エンジンモードで 1 回の録音に割り当てる区間の上限（設計 §2.2）。</summary>
    public int MaxSegments { get; init; } = 512;
}
