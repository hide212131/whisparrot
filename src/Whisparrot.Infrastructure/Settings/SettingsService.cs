using Whisparrot.Core;
using Whisparrot.Core.Settings;

namespace Whisparrot.Infrastructure.Settings;

/// <summary>
/// 現在の設定スナップショットと変更通知。読み込めなかった場合は既定値を保持するが書き込みは拒否する
/// （<see cref="LoadResult"/> に理由が残り、起動時の通知・ホットキー無効化に使う）。
/// </summary>
public sealed class SettingsService
{
    private readonly JsonSettingsStore _store;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsService(JsonSettingsStore store)
    {
        _store = store;
        LoadResult = store.Load();
        _current = LoadResult.Settings;
    }

    public SettingsLoadResult LoadResult { get; private set; }

    public string ConfigPath => _store.Path;

    public bool IsWritable => _store.CanSave;

    public AppSettings Current { get { lock (_lock) return _current; } }

    /// <summary>(旧, 新)。保存成功後に保存を呼んだスレッドで呼ばれる。UI は自分で Dispatcher へ戻す。</summary>
    public event Action<AppSettings, AppSettings>? Changed;

    public void Save(AppSettings next)
    {
        AppSettings old;
        lock (_lock)
        {
            _store.Save(next);
            old = _current;
            _current = next;
        }
        if (old != next) Changed?.Invoke(old, next);
    }

    public void Update(Func<AppSettings, AppSettings> mutate) => Save(mutate(Current));

    /// <summary>利用者の明示操作: 壊れた config.json を退避して既定値で再開する。</summary>
    public string BackupAndReset()
    {
        var backup = _store.BackupAndReset();
        LoadResult = new SettingsLoadResult(SettingsLoadStatus.Missing, AppSettings.Default, []);
        var old = Current;
        lock (_lock) _current = AppSettings.Default;
        Changed?.Invoke(old, AppSettings.Default);
        return backup;
    }
}

/// <summary>
/// 起動時のホットキー交渉順（設定のキー → 既定候補順）。<b>交渉で採用されたキーは保存しない</b>:
/// <c>config.json</c> の hotkey を書くのは、設定ウィンドウで利用者が明示的に選んで保存したときだけ。
/// 一時的な登録失敗（他アプリが一時的に Win+J を占有など）で次善のキーが保存され、以後それが先頭候補になり続ける粘着性を避ける。
/// </summary>
public sealed class SettingsHotkeyStore(SettingsService settings)
{
    public IReadOnlyList<Hotkey> NegotiationOrder() => HotkeyNegotiator.CandidatesFor(settings.Current.Hotkey);
}
