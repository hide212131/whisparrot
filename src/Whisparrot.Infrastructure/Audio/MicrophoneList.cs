using NAudio.CoreAudioApi;

namespace Whisparrot.Infrastructure.Audio;

public sealed record MicrophoneInfo(string Id, string Name);

/// <summary>有効な入力デバイスの一覧（設定画面用）。ID は MMDevice endpoint ID（設計 §8.1）。</summary>
public static class MicrophoneList
{
    public static IReadOnlyList<MicrophoneInfo> Enumerate()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new MicrophoneInfo(d.ID, d.FriendlyName)).ToList();
        }
        catch { return []; }
    }
}
