using System.Buffers.Binary;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Audio;

/// <summary>
/// 診断用: WAV（16 kHz / mono / PCM16）を「録音済みの音声」として返す <see cref="IRecorder"/>。
/// マイクの無い環境で、状態機械と認識ワーカーを本物のまま通すために使う。
/// </summary>
public sealed class WavFileRecorder(string path) : IRecorder
{
    public event Action<float>? LevelChanged;
#pragma warning disable CS0067 // ファイル入力は失敗しない
    public event Action<RecorderError>? Failed;
#pragma warning restore CS0067

    public bool Recording { get; private set; }

    public void Start()
    {
        Recording = true;
        LevelChanged?.Invoke(0.5f);
    }

    public Task<PcmAudio> StopAsync()
    {
        Recording = false;
        return Task.FromResult(ReadPcm(path));
    }

    public void Cancel() => Recording = false;

    public void Dispose() { }

    public static PcmAudio ReadPcm(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 12 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("WAV ではありません");
        int pos = 12, channels = 0, rate = 0, bits = 0, fmtTag = 0;
        while (pos + 8 <= d.Length)
        {
            var id = d.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 4));
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                fmtTag = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(body));
                channels = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (fmtTag != 1 || bits != 16 || channels != 1 || rate != PcmAudio.SampleRate)
                    throw new InvalidDataException($"16 kHz / mono / PCM16 の WAV が必要です（{rate} Hz, {channels} ch, {bits} bit）");
                var n = Math.Min(size, d.Length - body) / 2;
                var samples = new float[n];
                for (var i = 0; i < n; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(body + i * 2)) / 32768f;
                return new PcmAudio(samples);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("data チャンクがありません");
    }
}
