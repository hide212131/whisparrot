namespace Whisparrot.Infrastructure.Recognition;

/// <summary>入力に NaN / Infinity が含まれていた（送信前に失敗させる。設計 §6.1）。</summary>
public sealed class NonFiniteSampleException() : Exception("非有限のサンプルが含まれています");

/// <summary>
/// 認識直前の 16 kHz mono float から、メモリ上だけの RIFF/WAVE（PCM16 LE、44 byte ヘッダー、追加チャンクなし）を作る（設計 §6.1）。
/// 長さの計算は確保前に checked な 64 bit で行い、上限超過なら確保しない。
/// </summary>
public static class WavEncoder
{
    public const int SampleRate = 16000;
    public const int HeaderBytes = 44;

    /// <summary>送信する WAV ファイル全体の上限（25 MiB ではなく 25,000,000 byte。設計 §6.2）。</summary>
    public const long MaxWavBytes = 25_000_000;

    /// <summary><c>44 + 2N &lt;= 25,000,000</c> を満たす最大サンプル数（12,499,978 ≒ 13 分 1 秒）。</summary>
    public const long MaxSamples = (MaxWavBytes - HeaderBytes) / 2;

    public static long EncodedLength(long sampleCount) => checked(HeaderBytes + checked(sampleCount * 2));

    public static bool FitsLimit(long sampleCount) => sampleCount >= 0 && sampleCount <= MaxSamples;

    /// <summary>
    /// 有限値を [-1, 1] へ clamp し、負側 32768・正側 32767 の倍率で四捨五入（0.5 は 0 から遠ざける）して PCM16 へ飽和変換する。
    /// 上限超過は <see cref="ArgumentOutOfRangeException"/>、NaN / Infinity は <see cref="NonFiniteSampleException"/>。取消は <see cref="OperationCanceledException"/>。
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<float> samples, CancellationToken ct = default)
    {
        if (!FitsLimit(samples.Length)) throw new ArgumentOutOfRangeException(nameof(samples), "WAV の上限を超えています");
        var length = (int)EncodedLength(samples.Length);
        var wav = new byte[length];
        WriteHeader(wav, samples.Length);

        var o = HeaderBytes;
        for (var i = 0; i < samples.Length; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var v = samples[i];
            if (!float.IsFinite(v)) throw new NonFiniteSampleException();
            v = Math.Clamp(v, -1f, 1f);
            var scaled = v < 0 ? v * 32768.0 : v * 32767.0;
            var s = (short)Math.Clamp(Math.Round(scaled, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            wav[o++] = (byte)(s & 0xFF);
            wav[o++] = (byte)((s >> 8) & 0xFF);
        }
        return wav;
    }

    private static void WriteHeader(byte[] w, int sampleCount)
    {
        var dataBytes = sampleCount * 2;
        Ascii(w, 0, "RIFF");
        U32(w, 4, (uint)(w.Length - 8));
        Ascii(w, 8, "WAVE");
        Ascii(w, 12, "fmt ");
        U32(w, 16, 16);                  // fmt チャンク長
        U16(w, 20, 1);                   // PCM
        U16(w, 22, 1);                   // mono
        U32(w, 24, SampleRate);
        U32(w, 28, SampleRate * 2);      // byte rate 32,000
        U16(w, 32, 2);                   // block align
        U16(w, 34, 16);                  // bits per sample
        Ascii(w, 36, "data");
        U32(w, 40, (uint)dataBytes);
    }

    private static void Ascii(byte[] w, int o, string s) { for (var i = 0; i < s.Length; i++) w[o + i] = (byte)s[i]; }
    private static void U16(byte[] w, int o, ushort v) { w[o] = (byte)v; w[o + 1] = (byte)(v >> 8); }
    private static void U32(byte[] w, int o, uint v) { U16(w, o, (ushort)v); U16(w, o + 2, (ushort)(v >> 16)); }
}
