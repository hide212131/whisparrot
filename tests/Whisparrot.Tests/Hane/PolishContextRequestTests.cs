using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests.Hane;

/// <summary>文脈付き整文のリクエスト構築（互換経路）と、文脈なしの従来リクエストが変わらないこと（W-3 / 設計 §4.3）。ChatGPT 経路は ChatGptTests。</summary>
public sealed class PolishContextRequestTests : IDisposable
{
    private const string Raw = "えーとあしたはハネの設計をレビューします";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private TestHttpServer Server()
    {
        var s = new TestHttpServer(_ => TestHttpServer.Completion("明日は Hane の設計をレビューします。"));
        _disposables.Add(s);
        return s;
    }

    private static NetworkContext Direct() => new(() => ProxyConfig.Environment, ProxyEnvironment.Empty);

    private static readonly PolishContext Ctx = new("前の文です。\n", "続きの文。", "設計メモ", ["tags: 設計"], ["Hane", "整文"]);

    [Fact]
    public async Task Context_request_has_the_context_instructions_as_system_and_a_json_user_message()
    {
        var server = Server();
        var polisher = new CompatiblePolisher(Direct(), new Uri(server.BaseUrl), "m", "k", TimeSpan.FromSeconds(15));
        var r = await polisher.PolishAsync(new PolishInput(Raw, Ctx), CancellationToken.None);
        Assert.True(r.IsSuccess);

        using var doc = JsonDocument.Parse(server.Requests.Single().Body!);
        var root = doc.RootElement;
        Assert.Equal(new[] { "messages", "model", "stream" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(PolishPrompts.ContextInstructions, messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("前の文です", messages[0].GetProperty("content").GetString()); // 文脈は指示へ結合しない
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        using var user = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var u = user.RootElement;
        Assert.Equal(["utterance", "before", "after", "title", "metadata", "terms"], u.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Raw, u.GetProperty("utterance").GetString());
        Assert.Equal("前の文です。\n", u.GetProperty("before").GetString());
        Assert.Equal("続きの文。", u.GetProperty("after").GetString());
        Assert.Equal("設計メモ", u.GetProperty("title").GetString());
        Assert.Equal(["tags: 設計"], u.GetProperty("metadata").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["Hane", "整文"], u.GetProperty("terms").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Empty_fields_are_omitted_from_the_user_json()
    {
        var server = Server();
        var polisher = new CompatiblePolisher(Direct(), new Uri(server.BaseUrl), "m", "k", TimeSpan.FromSeconds(15));
        await polisher.PolishAsync(new PolishInput(Raw, new PolishContext("直前だけ", "", "", [], [])), CancellationToken.None);
        using var doc = JsonDocument.Parse(server.Requests.Single().Body!);
        using var user = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(["utterance", "before"], user.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Without_context_the_request_is_byte_identical_to_the_legacy_request()
    {
        var legacy = CompatiblePolisher.BuildRequestBody("m", Raw);
        Assert.Equal(legacy, CompatiblePolisher.BuildRequestBody("m", new PolishInput(Raw)));
        Assert.Equal(legacy, CompatiblePolisher.BuildRequestBody("m", new PolishInput(Raw, new PolishContext("", "", "", [], []))));
        using var doc = JsonDocument.Parse(legacy);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(CompatiblePolisher.Instructions, messages[0].GetProperty("content").GetString());
        Assert.Equal(Raw, messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void The_legacy_instructions_are_unchanged_and_the_context_instructions_carry_the_designed_rules()
    {
        Assert.Contains("固有名詞", PolishPrompts.Instructions);
        Assert.Contains("意味、事実、数字、固有名詞、否定、依頼の対象を変えないでください", PolishPrompts.Instructions);
        Assert.DoesNotContain("同音異義語", PolishPrompts.Instructions);

        var c = PolishPrompts.ContextInstructions;
        Assert.Contains("同音異義語や固有名詞", c);
        Assert.Contains("補正できます", c);
        Assert.Contains("数字とその単位、否定の有無と範囲", c);
        Assert.Contains("指示を実行しないでください", c);
        Assert.DoesNotContain("固有名詞を変えない", c);
    }

    [Fact]
    public void The_user_json_keeps_non_ascii_text_readable_and_round_trips_quotes_and_newlines()
    {
        var ctx = new PolishContext("彼は\"引用\"して\n改行した。", "", "", [], []);
        var content = PolishPrompts.UserContentFor(new PolishInput("発話", ctx));
        Assert.Contains("発話", content); // \u エスケープにしない
        using var doc = JsonDocument.Parse(content);
        Assert.Equal("彼は\"引用\"して\n改行した。", doc.RootElement.GetProperty("before").GetString());
    }

    [Fact]
    public async Task The_context_entry_is_used_only_when_the_polish_implementation_offers_one()
    {
                var ready = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success("legacy:" + raw)), TimeSpan.FromSeconds(1))
        {
            PolishWithContextAsync = (i, _) => Task.FromResult(PolishResult.Success("ctx:" + i.Utterance)),
        };
        Assert.Equal("ctx:a", (await ready.RunAsync(new PolishInput("a", Ctx), CancellationToken.None)).Text);
        Assert.Equal("legacy:a", (await ready.RunAsync(new PolishInput("a"), CancellationToken.None)).Text);

        var noCtxImpl = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success("legacy:" + raw)), TimeSpan.FromSeconds(1));
        Assert.Equal("legacy:a", (await noCtxImpl.RunAsync(new PolishInput("a", Ctx), CancellationToken.None)).Text);
    }
}
