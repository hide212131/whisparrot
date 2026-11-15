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

/// <summary><see cref="IAdoptedHotkeyStore"/> の実物。交渉の結果、設定のキーと違うものを採用したときだけ保存する。</summary>
public sealed class SettingsHotkeyStore(SettingsService settings) : IAdoptedHotkeyStore
{
    public void Record(Hotkey? adopted, IReadOnlyList<(string Display, bool Ok, int Error)> attempts)
    {
        if (adopted is null || !settings.IsWritable) return; // 全滅や読み込み失敗中は何も書かない
        if (HotkeyFormat.SameKey(adopted, settings.Current.Hotkey)) return;
        settings.Update(s => s with { Hotkey = adopted });
    }
}
