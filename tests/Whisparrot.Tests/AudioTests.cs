using NAudio.Wave;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Audio;

namespace Whisparrot.Tests;

public sealed class AudioTests
{
    [Fact]
    public void Stereo_pcm16_is_averaged_to_mono_float()
    {
        var fmt = new WaveFormat(16000, 16, 2);
        var buf = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(buf, 0);   // L = 0.5
        BitConverter.GetBytes((short)-16384).CopyTo(buf, 2);  // R = -0.5
        BitConverter.GetBytes((short)16384).CopyTo(buf, 4);
        BitConverter.GetBytes((short)16384).CopyTo(buf, 6);
        var mono = new List<float>();
        WasapiRecorder.AppendMono(buf, buf.Length, fmt, mono);
        Assert.Equal(2, mono.Count);
        Assert.Equal(0f, mono[0], 4);
        Assert.Equal(0.5f, mono[1], 4);
    }

    [Fact]
    public void Float32_mono_is_passed_through()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buf = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(buf, 0);
        BitConverter.GetBytes(-0.75f).CopyTo(buf, 4);
        var mono = new List<float>();
        WasapiRecorder.AppendMono(buf, buf.Length, fmt, mono);
        Assert.Equal([0.25f, -0.75f], mono);
    }

    [Fact]
    public void Partial_trailing_frame_is_ignored()
    {
        var fmt = new WaveFormat(16000, 16, 2);
        var mono = new List<float>();
        WasapiRecorder.AppendMono(new byte[6], 6, fmt, mono); // 1 フレーム + 端数 2 バイト
        Assert.Single(mono);
    }

    [Fact]
    public void Resampling_48k_to_16k_keeps_duration_and_a_tone()
    {
        const int rate = 48000;
        var tone = Enumerable.Range(0, rate).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate))).ToArray();
        var outSamples = WasapiRecorder.Resample(tone, rate);
        Assert.InRange(outSamples.Length, 15900, 16100);
        Assert.InRange(outSamples.Skip(2000).Take(10000).Max(), 0.4f, 0.6f);
    }

    [Fact]
    public void Resampling_16k_is_identity()
    {
        float[] x = [0.1f, 0.2f];
        Assert.Same(x, WasapiRecorder.Resample(x, 16000));
    }

    [Fact]
    public void Wav_reader_accepts_16k_mono_pcm16_and_rejects_other_formats()
    {
        string Make(int rate, short channels)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
            using (var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels)))
            {
                w.WriteData(new byte[] { 0, 0, 0, 64, 0, 192 }, 0, 6 * (channels == 1 ? 1 : 1));
            }
            return path;
        }
        var ok = Make(16000, 1);
        try
        {
            var pcm = WavFileRecorder.ReadPcm(ok);
            Assert.Equal(3, pcm.Samples.Length);
            Assert.Equal(0.5f, pcm.Samples[1], 3);
            Assert.Equal(-0.5f, pcm.Samples[2], 3);
        }
        finally { File.Delete(ok); }

        var bad = Make(44100, 1);
        try { Assert.Throws<InvalidDataException>(() => WavFileRecorder.ReadPcm(bad)); }
        finally { File.Delete(bad); }
    }
}
