using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

public sealed class UnsupportedAudioException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 音声ファイル（WAV / m4a）を認識の入力（16 kHz・モノラル・float）へ変換する。WAV は NAudio、m4a は Media Foundation。
/// それ以外の形式・壊れたファイルは <see cref="UnsupportedAudioException"/>。
/// </summary>
public static class AudioFileDecoder
{
    public static string FormatOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static PcmAudio Read(string path)
    {
        var format = FormatOf(path);
        if (format is not ("wav" or "m4a"))
            throw new UnsupportedAudioException($"未対応の形式です（{(format.Length == 0 ? "拡張子なし" : "." + format)}）。対応: .wav / .m4a");
        if (!File.Exists(path)) throw new FileNotFoundException("入力ファイルが見つかりません", path);
        try
        {
            using WaveStream reader = format == "wav" ? new WaveFileReader(path) : new MediaFoundationReader(path);
            return Convert(reader);
        }
        catch (Exception ex) when (ex is not (UnsupportedAudioException or FileNotFoundException))
        {
            throw new UnsupportedAudioException($"音声を読み込めません（{ex.GetType().Name}）", ex);
        }
    }

    /// <summary>任意のサンプルレート・チャンネル数・PCM / float の波形を 16 kHz モノラルへ変換する。</summary>
    public static PcmAudio Convert(IWaveProvider source)
    {
        var format = source.WaveFormat;
        if (format is WaveFormatExtensible ext) format = ext.ToStandardWaveFormat();
        if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
            throw new UnsupportedAudioException($"未対応の WAV の符号化です（{format.Encoding}）");
        var provider = ReferenceEquals(format, source.WaveFormat) ? source : new Reformat(source, format);
        var interleaved = ReadAll(provider.ToSampleProvider());
        var mono = Downmix(interleaved, format.Channels);
        return new PcmAudio(Resample(mono, format.SampleRate));
    }

    public static float[] Downmix(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    public static float[] Resample(float[] mono, int sampleRate)
    {
        if (sampleRate == PcmAudio.SampleRate || mono.Length == 0) return mono;
        var resampler = new WdlResamplingSampleProvider(new ArraySamples(mono, sampleRate), PcmAudio.SampleRate);
        return ReadAll(resampler);
    }

    private static float[] ReadAll(ISampleProvider p)
    {
        var all = new List<float>();
        var buf = new float[p.WaveFormat.SampleRate * p.WaveFormat.Channels];
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    /// <summary>拡張形式の WAV を標準形式として読み直すための薄い包み。</summary>
    private sealed class Reformat(IWaveProvider inner, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }

    private sealed class ArraySamples(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _pos);
            Array.Copy(samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
