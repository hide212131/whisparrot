using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Polish;

/// <summary>
/// 録音開始時の設定から整文の構えを作る（設計 §3.2: 設定変更は次のセッションから）。
/// compatible で接続情報が足りない／使えないときは整文を試みさせず <see cref="PolishSetup.Unconfigured"/> にする。
/// chatgpt は未実装（T13）なので同様に送信しない。設定値自体は変更しない。
/// </summary>
public sealed class SettingsTextPolisher(Func<AppSettings> settings, ISecretProtector protector, NetworkContext network) : ITextPolisher
{
    public PolishSetup Prepare()
    {
        var cleanup = settings().Cleanup;
        switch (cleanup.Provider)
        {
            case CleanupProvider.Off:
                return new PolishSetup.Off();
            case CleanupProvider.ChatGpt:
                return new PolishSetup.Unconfigured("ChatGPT 経由の整文は未対応です");
        }

        var c = cleanup.Compatible;
        if (c.BaseUrl.Trim().Length == 0) return new PolishSetup.Unconfigured("ベース URL が未設定です");
        if (CompatibleEndpointUrl.Validate(c.BaseUrl, out var uri) is not null || uri is null)
            return new PolishSetup.Unconfigured("ベース URL が不正です");
        if (c.Model.Trim().Length == 0) return new PolishSetup.Unconfigured("モデル名が未設定です");

        if (!protector.TryUnprotect(c.ApiKeyDpapi, out var key))
            return new PolishSetup.Unconfigured("API キーを復号できません。設定で入力し直してください");
        // 認証不要のローカルサーバーだけキー無しを許す（設計 §7.2）。外部の送信先ではキー無しの送信をしない。
        if (string.IsNullOrEmpty(key) && !RoutingProxy.IsLoopback(uri)) return new PolishSetup.Unconfigured("API キーが未設定です");

        var timeout = TimeSpan.FromSeconds(cleanup.TimeoutSeconds);
        var polisher = new CompatiblePolisher(network, uri, c.Model.Trim(), key, timeout);
        return new PolishSetup.Ready(polisher.PolishAsync, timeout);
    }
}
