using System.Threading.Channels;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

/// <summary>
/// 診断用: 16 kHz / mono / PCM16 の WAV を連結し、<paramref name="speed"/> 倍速で「録音中の連続 PCM」として流す <see cref="IStreamingRecorder"/>。
/// マイクの無い環境で、区間の切り出し・順序・停止時のフラッシュを本物のコーディネーターに通すために使う。音声を流し終えても、停止されるまでは録音中のまま。
/// </summary>
public sealed class WavStreamingRecorder(IReadOnlyList<string> paths, double speed = 1.0) : IStreamingRecorder
{
    private const int FrameSamples = 1600; // 100 ms
    private readonly CancellationTokenSource _cts = new();
    private Channel<PcmFrame>? _channel;
    private long _fed;

    public event Action<float>? LevelChanged;
#pragma warning disable CS0067 // ファイル入力は失敗しない
    public event Action<RecorderError>? Failed;
#pragma warning restore CS0067

    public long DroppedSamples => 0;

    /// <summary>これまでに流したサンプル数。</summary>
    public long FedSamples => Interlocked.Read(ref _fed);

    public void Start() => throw new NotSupportedException("連続録音専用です");

    public ChannelReader<PcmFrame> StartStreaming()
    {
        var pcm = paths.SelectMany(p => WavFileRecorder.ReadPcm(p).Samples).ToArray();
        var channel = _channel = Channel.CreateUnbounded<PcmFrame>();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                for (var pos = 0; pos < pcm.Length && !token.IsCancellationRequested; pos += FrameSamples)
                {
                    var n = Math.Min(FrameSamples, pcm.Length - pos);
                    var due = TimeSpan.FromSeconds(pos / (double)PcmAudio.SampleRate / speed);
                    var wait = due - System.Diagnostics.Stopwatch.GetElapsedTime(start);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                    channel.Writer.TryWrite(new PcmFrame(pos, pcm.AsSpan(pos, n).ToArray()));
                    Interlocked.Exchange(ref _fed, pos + n);
                    LevelChanged?.Invoke(0.5f);
                }
            }
            catch (OperationCanceledException) { }
        });
        return channel.Reader;
    }

    public Task StopStreamingAsync()
    {
        _cts.Cancel();
        _channel?.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<PcmAudio> StopAsync() => throw new NotSupportedException("連続録音専用です");

    public void Cancel()
    {
        _cts.Cancel();
        _channel?.Writer.TryComplete();
    }

    public void Dispose() => Cancel();
}
