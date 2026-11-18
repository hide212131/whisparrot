namespace Whisparrot.Infrastructure.ChatGpt;

/// <summary>
/// 公式 Sign in with ChatGPT（直接 OAuth）の接続先。製品は <see cref="Official"/> だけを使い、設定からは変えられない。
/// 試験・診断だけがループバックのモックへ差し替える。<see cref="RedirectPort"/> 0 は空きポートを使う（試験用）。
/// </summary>
public sealed record ChatGptEndpoints(Uri AuthorizeUrl, Uri TokenUrl, Uri ResponsesUrl, string Resource, int RedirectPort)
{
    public const string DynamicClientId = "dynamic_agent_client";
    public const string AgentName = "whisparrot";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public const string RequiredScope = "chatgpt.tokens.use.direct";
    public const int OfficialRedirectPort = 1455;

    public static ChatGptEndpoints Official { get; } = new(
        new Uri("https://auth.openai.com/api/accounts/authorize"),
        new Uri("https://auth.openai.com/api/accounts/oauth/token"),
        new Uri("https://api.openai.com/v1/responses"),
        "https://api.openai.com/v1",
        OfficialRedirectPort);
}
