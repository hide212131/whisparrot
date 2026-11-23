using System.Runtime.InteropServices;
using Whisparrot.Core.Update;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Update;

public enum UpdateState { Idle, Checking, Available, Downloading, Ready, Failed }

/// <summary>
/// 更新の確認・取得の状態機械。確認の失敗は静かに諦める（<see cref="Message"/> に残すだけで、通知は出さない）。
/// ダウンロードの失敗（ダイジェスト不一致を含む）は <see cref="UpdateState.Failed"/> と理由を返し、UI が表示する。
/// </summary>
public sealed class UpdateService
{
    private readonly SettingsService _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ReleaseInfo>>> _list;
    private readonly Func<UpdateCandidate, string, CancellationToken, Task> _download;
    private readonly SemVer? _current;
    private readonly string _arch;
    private readonly SwapPlan _plan;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService(SettingsService settings, ReleaseClient client, SemVer? current, SwapPlan plan)
        : this(settings, client.ListAsync, (c, dest, ct) => client.DownloadAsync(c, dest, null, ct), current, CurrentArch(), plan, () => DateTimeOffset.UtcNow) { }

    public UpdateService(SettingsService settings, Func<CancellationToken, Task<IReadOnlyList<ReleaseInfo>>> list,
        Func<UpdateCandidate, string, CancellationToken, Task> download, SemVer? current, string arch, SwapPlan plan, Func<DateTimeOffset> now)
    {
        _settings = settings; _list = list; _download = download; _current = current; _arch = arch; _plan = plan; _now = now;
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateCandidate? Candidate { get; private set; }
    public string? Message { get; private set; }
    public SwapPlan Plan => _plan;

    /// <summary>状態が変わったとき（任意のスレッド）。<paramref name="newlyAvailable"/> は、新しい版を初めて見つけたとき。</summary>
    public event Action<bool>? Changed;

    public static string CurrentArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var a => a.ToString().ToLowerInvariant(),
    };

    /// <summary>確認する。ネットワークの失敗は例外にせず、候補なし（または以前の候補を維持）で戻る。</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_current is null) { Set(UpdateState.Idle, null, "この版では更新を確認できません"); return; }
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (State is UpdateState.Downloading or UpdateState.Ready) return;
            Set(UpdateState.Checking, Candidate, null);
            RecordAttempt(success: false);
            IReadOnlyList<ReleaseInfo> releases;
            try { releases = await _list(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Set(Candidate is null ? UpdateState.Idle : UpdateState.Available, Candidate, "確認できませんでした"); // 静かに諦める
                return;
            }
            RecordAttempt(success: true);
            var found = ReleaseSelector.Select(releases, _current, _settings.Current.Updates.IncludePrerelease, _arch);
            var isNew = found is not null && found.Tag != Candidate?.Tag;
            Set(found is null ? UpdateState.Idle : UpdateState.Available, found, found is null ? "最新の版です" : null, isNew);
        }
        finally { _gate.Release(); }
    }

    /// <summary>候補を取得して検証し、<see cref="SwapPlan.NewPath"/> に置く。成功で <see cref="UpdateState.Ready"/>。</summary>
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        if (Candidate is not { } c || State is not (UpdateState.Available or UpdateState.Failed)) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Set(UpdateState.Downloading, c, null);
            try
            {
                await _download(c, _plan.NewPath, ct).ConfigureAwait(false);
                Set(UpdateState.Ready, c, null);
            }
            catch (OperationCanceledException) { Set(UpdateState.Available, c, null); }
            catch (UpdateException ex) { Set(UpdateState.Failed, c, ex.Message); }
            catch (Exception ex) { Set(UpdateState.Failed, c, $"更新のダウンロードに失敗しました（{ex.GetType().Name}）"); }
        }
        finally { _gate.Release(); }
    }

    private void Set(UpdateState state, UpdateCandidate? candidate, string? message, bool newlyAvailable = false)
    {
        State = state; Candidate = candidate; Message = message;
        Changed?.Invoke(newlyAvailable);
    }

    private void RecordAttempt(bool success)
    {
        if (!_settings.IsWritable) return;
        try
        {
            var now = _now();
            _settings.Update(s => s with { Updates = s.Updates with { LastCheckAttemptUtc = now, LastCheckSuccessUtc = success ? now : s.Updates.LastCheckSuccessUtc } });
        }
        catch (Exception) { } // 日時は表示用の補助。保存できなくても確認は続ける
    }
}
