using System.Threading.Channels;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

/// <summary>
/// 連続録音の有界バッファ（設計 §3・§7）。キャプチャのコールバックから <see cref="Offer"/> でフレームを渡し、読み手が <see cref="Reader"/> から取る。
/// 上限はサンプル数（WASAPI の通知の大きさに依らず秒数で決まる）。満杯のときは新しいフレームの受け入れを省いて数え、コールバックを待たせない。
/// サンプル位置は受け入れを省いた分も進めるので、読み手は <see cref="PcmFrame.StartSample"/> の不連続から音声の欠けを検出できる。書き手は一つ（キャプチャスレッド）。
/// </summary>
internal sealed class FrameBuffer(int maxQueuedSamples)
{
    private readonly Channel<PcmFrame> _channel = Channel.CreateUnbounded<PcmFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private long _position;
    private long _dropped;
    private long _queued;
    private ChannelReader<PcmFrame>? _reader;

    public ChannelReader<PcmFrame> Reader => _reader ??= new CountingReader(_channel.Reader, this);

    /// <summary>受け入れを省いたサンプル数の累計。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>ここまでに渡したサンプル数（省いた分を含む）。</summary>
    public long Position => Interlocked.Read(ref _position);

    /// <summary>読み手がまだ取っていないサンプル数。</summary>
    public long Queued => Interlocked.Read(ref _queued);

    /// <summary>フレームを渡す。受け入れたら true、満杯で省いたら false。</summary>
    public bool Offer(float[] samples)
    {
        if (samples.Length == 0) return true;
        var start = _position;
        Interlocked.Add(ref _position, samples.Length);
        if (Interlocked.Read(ref _queued) + samples.Length > maxQueuedSamples)
        {
            Interlocked.Add(ref _dropped, samples.Length);
            return false;
        }
        Interlocked.Add(ref _queued, samples.Length);
        if (_channel.Writer.TryWrite(new PcmFrame(start, samples))) return true;
        Interlocked.Add(ref _queued, -samples.Length); // 完了後の書き込み
        Interlocked.Add(ref _dropped, samples.Length);
        return false;
    }

    /// <summary>これ以上フレームが来ないことを読み手へ知らせる（読み手は残りを読み切ってから終わる）。</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private sealed class CountingReader(ChannelReader<PcmFrame> inner, FrameBuffer owner) : ChannelReader<PcmFrame>
    {
        public override Task Completion => inner.Completion;
        public override bool CanCount => false;

        public override bool TryRead(out PcmFrame item)
        {
            if (!inner.TryRead(out item!)) return false;
            Interlocked.Add(ref owner._queued, -item.Samples.Length);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => inner.WaitToReadAsync(cancellationToken);
    }
}
