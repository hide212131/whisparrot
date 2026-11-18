using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Infrastructure.Polish;

/// <summary>
/// 録音開始時の設定から整文の構えを作る（設計 §3.2: 設定変更は次のセッションから）。
/// compatible で接続情報が足りない／使えないときは整文を試みさせず <see cref="PolishSetup.Unconfigured"/> にする。
/// chatgpt は未ログイン・token 無効・モデル未指定なら同様に送信しない（勝手にブラウザも開かない）。設定値自体は変更しない。
/// </summary>
public sealed class SettingsTextPolisher(Func<AppSettings> settings, ISecretProtector protector, NetworkContext network, ChatGptAuthService chatGpt) : ITextPolisher
{
    public PolishSetup Prepare()
    {
        var cleanup = settings().Cleanup;
        switch (cleanup.Provider)
        {
            case CleanupProvider.Off:
                return new PolishSetup.Off();
            case CleanupProvider.ChatGpt:
                return PrepareChatGpt(cleanup);
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

    private PolishSetup PrepareChatGpt(CleanupSettings cleanup)
    {
        if (chatGpt.CheckReady() is { } reason) return new PolishSetup.Unconfigured(reason);
        var model = cleanup.ChatGpt.Model.Trim();
        if (model.Length == 0) return new PolishSetup.Unconfigured("ChatGPT のモデル名が未設定です。設定で指定してください");
        var timeout = TimeSpan.FromSeconds(cleanup.TimeoutSeconds);
        return new PolishSetup.Ready(new ChatGptPolisher(network, chatGpt, model, timeout).PolishAsync, timeout);
    }
}
