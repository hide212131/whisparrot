using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

/// <summary>
/// WASAPI 共有モードのマイク録音（既定の通信デバイスに追従）。入力を float mono に落とし、
/// 停止時に 16 kHz へ変換して返す。どの終了経路（停止・取消・エラー・Dispose）でもデバイスを解放する。
/// </summary>
public sealed class WasapiRecorder : IRecorder
{
    /// <summary>停止要求後も取り込みを続ける時間。WASAPI のバッファに残る語尾を回収する。</summary>
    internal static readonly TimeSpan TailGrace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private List<float>? _mono;
    private TaskCompletionSource? _stopped;
    private bool _userStopping;
    private long _lastLevelTicks;

    public event Action<float>? LevelChanged;
    public event Action<RecorderError>? Failed;

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) throw new RecorderException(RecorderErrorKind.Other, "すでに録音中です");
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _capture = new WasapiCapture(_device);
                _format = _capture.WaveFormat;
                _mono = new List<float>(_format.SampleRate * 30);
                _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _userStopping = false;
                _lastLevelTicks = 0;
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();
            }
            catch (Exception ex)
            {
                ReleaseLocked();
                throw Translate(ex);
            }
        }
    }

    public async Task<PcmAudio> StopAsync()
    {
        TaskCompletionSource stopped;
        lock (_gate)
        {
            if (_capture is null || _stopped is null) throw new RecorderException(RecorderErrorKind.Other, "録音していません");
            stopped = _stopped;
        }

        await Task.Delay(TailGrace).ConfigureAwait(false);

        lock (_gate)
        {
            if (_capture is null) throw new RecorderException(RecorderErrorKind.Disconnected, "録音が中断されました");
            _userStopping = true;
            try { _capture.StopRecording(); } catch { /* 下で解放する */ }
        }
        await Task.WhenAny(stopped.Task, Task.Delay(StopTimeout)).ConfigureAwait(false);

        float[] mono;
        int rate;
        lock (_gate)
        {
            mono = _mono?.ToArray() ?? [];
            rate = _format?.SampleRate ?? PcmAudio.SampleRate;
            ReleaseLocked();
        }
        return new PcmAudio(Resample(mono, rate));
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _userStopping = true;
            try { _capture?.StopRecording(); } catch { }
            ReleaseLocked();
        }
    }

    public void Dispose() => Cancel();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float level;
        lock (_gate)
        {
            if (_mono is null || _format is null) return;
            var start = _mono.Count;
            AppendMono(e.Buffer, e.BytesRecorded, _format, _mono);
            level = Rms(_mono, start);
        }
        var now = Environment.TickCount64;
        if (now - _lastLevelTicks >= LevelInterval.TotalMilliseconds)
        {
            _lastLevelTicks = now;
            LevelChanged?.Invoke(level);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        bool user;
        lock (_gate)
        {
            user = _userStopping;
            if (e.Exception is not null && !user)
            {
                // 切断などの致命エラー: デバイスを解放して通知する。
                ReleaseLocked();
            }
            else
            {
                _stopped?.TrySetResult();
            }
        }
        if (e.Exception is not null && !user)
        {
            Failed?.Invoke(new RecorderError(RecorderErrorKind.Disconnected, "マイクが切断されました"));
        }
    }

    private void ReleaseLocked()
    {
        var capture = _capture;
        var device = _device;
        _capture = null;
        _device = null;
        _mono = null;
        _format = null;
        var stopped = _stopped;
        _stopped = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try { capture.Dispose(); } catch { }
        }
        try { device?.Dispose(); } catch { }
        stopped?.TrySetResult();
    }

    private static RecorderException Translate(Exception ex) => ex switch
    {
        RecorderException r => r,
        UnauthorizedAccessException => new RecorderException(RecorderErrorKind.AccessDenied, "マイクへのアクセスが許可されていません", ex),
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x80070005 =>
            new RecorderException(RecorderErrorKind.AccessDenied, "マイクへのアクセスが許可されていません", ex),
        _ => new RecorderException(RecorderErrorKind.DeviceUnavailable, "マイクを使えません（入力デバイスを確認してください）", ex),
    };

    /// <summary>キャプチャ形式（float32 / PCM16 / 24 / 32）を float に直し、チャンネルを平均して mono にする。</summary>
    internal static void AppendMono(byte[] buffer, int bytes, WaveFormat format, List<float> mono)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frameBytes = bytesPerSample * channels;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        for (var pos = 0; pos + frameBytes <= bytes; pos += frameBytes)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = pos + c * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(buffer, o),
                    4 => BitConverter.ToInt32(buffer, o) / 2147483648f,
                    3 => ((buffer[o] << 8) | (buffer[o + 1] << 16) | (buffer[o + 2] << 24)) / 2147483648f,
                    2 => BitConverter.ToInt16(buffer, o) / 32768f,
                    _ => 0f,
                };
            }
            mono.Add(sum / channels);
        }
    }

    private static float Rms(List<float> samples, int start)
    {
        var n = samples.Count - start;
        if (n <= 0) return 0;
        double acc = 0;
        for (var i = start; i < samples.Count; i++) acc += samples[i] * samples[i];
        var rms = Math.Sqrt(acc / n);
        return (float)Math.Min(1.0, Math.Sqrt(rms) * 1.5); // 小さい声でも動くよう平方根で持ち上げる
    }

    internal static float[] Resample(float[] mono, int sourceRate)
    {
        if (sourceRate == PcmAudio.SampleRate || mono.Length == 0) return mono;
        var resampler = new WdlResamplingSampleProvider(new ArraySampleProvider(mono, sourceRate), PcmAudio.SampleRate);
        var output = new List<float>((int)((long)mono.Length * PcmAudio.SampleRate / sourceRate) + 16);
        var buf = new float[4096];
        int n;
        while ((n = resampler.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++) output.Add(buf[i]);
        }
        return output.ToArray();
    }

    private sealed class ArraySampleProvider(float[] data, int rate) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
