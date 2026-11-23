using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

/// <summary>
/// 連続録音用の 16 kHz 変換。フレームごとにリサンプラーを作り直さず、一つの <see cref="WdlResamplingSampleProvider"/> の状態を録音の間じゅう維持する
/// （フレームの境目でクリックや位相のずれを出さない。設計 §1）。16 kHz の入力はそのまま通す。
/// </summary>
internal sealed class StreamingResampler
{
    private readonly QueueProvider? _queue;
    private readonly WdlResamplingSampleProvider? _resampler;
    private readonly float[] _out = new float[4096];

    public StreamingResampler(int sourceRate)
    {
        if (sourceRate == PcmAudio.SampleRate) return;
        _queue = new QueueProvider(sourceRate);
        _resampler = new WdlResamplingSampleProvider(_queue, PcmAudio.SampleRate);
    }

    /// <summary>入力（元のサンプルレートの mono）を取り込み、変換できた 16 kHz のサンプルを返す。</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (_queue is null || _resampler is null) return input.ToArray();
        _queue.Add(input);
        var result = new List<float>((int)((long)input.Length * PcmAudio.SampleRate / _queue.WaveFormat.SampleRate) + 16);
        int n;
        while ((n = _resampler.Read(_out, 0, _out.Length)) > 0)
        {
            for (var i = 0; i < n; i++) result.Add(_out[i]);
        }
        return result.ToArray();
    }

    private sealed class QueueProvider(int rate) : ISampleProvider
    {
        private float[] _buf = new float[8192];
        private int _count;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public void Add(ReadOnlySpan<float> data)
        {
            if (_count + data.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _count + data.Length));
            data.CopyTo(_buf.AsSpan(_count));
            _count += data.Length;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _count);
            Array.Copy(_buf, 0, buffer, offset, n);
            Array.Copy(_buf, n, _buf, 0, _count - n);
            _count -= n;
            return n;
        }
    }
}
