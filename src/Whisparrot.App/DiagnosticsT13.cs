using System.IO;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// T13（ChatGPT adapter）の非対話診断。製品は常に公式の接続先を使い、ここだけが --mock でループバックのモックへ向ける。
///   --chatgpt-login --token-file F --mock URL [--redirect-port N] [--paste-file F]
///        BeginLogin → 認可 URL を出力 → コールバック（モックの疑似 GET、または --paste-file の内容）待ち → 完了。ブラウザは開かない
///   --chatgpt-status --token-file F [--mock URL]    接続状態（token の値は出さない）
///   --chatgpt-logout --token-file F                 ログアウト（ファイル削除）
/// --cleanup-probe / --pipeline-file は --chatgpt-token-file / --chatgpt-mock で同じ保存域・モックを使う。
/// </summary>
internal static partial class Diagnostics
{
    private static ChatGptAuthService MakeChatGpt(string tokenFile, string? mock, int redirectPort, NetworkContext? network = null)
    {
        var protector = new DpapiSecretProtector();
        network ??= new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Capture());
        ChatGptEndpoints? ep = null;
        if (mock is not null)
        {
            var b = mock.TrimEnd('/');
            ep = new ChatGptEndpoints(new Uri(b + "/api/accounts/authorize"), new Uri(b + "/api/accounts/oauth/token"), new Uri(b + "/v1/responses"), b + "/v1", redirectPort);
        }
        return new ChatGptAuthService(network, new ChatGptTokenStore(tokenFile, protector), ep);
    }

    /// <summary>cleanup / pipeline の診断用。token ファイルの既定は config.json の隣。</summary>
    private static ChatGptAuthService ChatGptFor(string[] args, string configPath, NetworkContext network, ISecretProtector _) =>
        MakeChatGpt(Arg(args, "--chatgpt-token-file") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "chatgpt-token.json"),
            Arg(args, "--chatgpt-mock"), 0, network);

    public static int ChatGptLogin(string[] args)
    {
        var svc = MakeChatGpt(Arg(args, "--token-file") ?? throw new ArgumentException("--token-file が必要です"),
            Arg(args, "--mock"), int.TryParse(Arg(args, "--redirect-port"), out var port) ? port : 0);
        ChatGptAuthService.ChatGptLoginAttempt attempt;
        try { attempt = svc.BeginLogin(); }
        catch (ChatGptLoginException ex) { Console.WriteLine($"login=FAIL kind={ex.Kind} message={ex.Message}"); return 1; }
        using (attempt)
        {
            Console.WriteLine("authorize_url=" + attempt.AuthorizeUrl.AbsoluteUri);
            if (Arg(args, "--paste-file") is { } pasteFile)
            {
                // ファイルの内容が変わるたびに貼り付けとして渡す（不正な貼り付けの後にやり直せることの確認用）。
                _ = Task.Run(async () =>
                {
                    string? last = null;
                    while (!attempt.Completion.IsCompleted)
                    {
                        await Task.Delay(100);
                        string? text;
                        try { text = File.Exists(pasteFile) ? File.ReadAllText(pasteFile) : null; } catch (IOException) { continue; }
                        if (text is null || text == last) continue;
                        last = text;
                        try { attempt.SubmitPasted(text); Console.WriteLine("paste=accepted"); }
                        catch (ChatGptLoginException ex) { Console.WriteLine($"paste=rejected kind={ex.Kind}"); }
                    }
                });
            }
            try
            {
                if (!attempt.Completion.Wait(TimeSpan.FromSeconds(60))) { Console.WriteLine("login=FAIL kind=ProbeTimeout"); return 1; }
                var st = attempt.Completion.Result;
                Console.WriteLine($"login=OK status={st.State}");
                return 0;
            }
            catch (AggregateException ae) when (ae.InnerException is ChatGptLoginException ex)
            {
                Console.WriteLine($"login=FAIL kind={ex.Kind} message={ex.Message}");
                return 1;
            }
        }
    }

    public static int ChatGptStatusProbe(string[] args)
    {
        var svc = MakeChatGpt(Arg(args, "--token-file") ?? throw new ArgumentException("--token-file が必要です"), Arg(args, "--mock"), 0);
        var st = svc.GetStatus();
        Console.WriteLine($"status={st.State} detail={st.Detail ?? "-"}");
        var load = new ChatGptTokenStore(Arg(args, "--token-file")!, new DpapiSecretProtector()).Load();
        if (load.Tokens is { } t)
            Console.WriteLine($"client_id_len={t.ClientId.Length} installation_id={t.InstallationId} scope_ok={t.HasRequiredScope} expires_in_s={(int)(t.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds}");
        return 0;
    }

    public static int ChatGptLogout(string[] args)
    {
        var tokenFile = Arg(args, "--token-file") ?? throw new ArgumentException("--token-file が必要です");
        MakeChatGpt(tokenFile, null, 0).Logout();
        Console.WriteLine($"logout=OK file_exists={File.Exists(tokenFile)}");
        return 0;
    }
}
