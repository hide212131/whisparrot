using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Voice;

/// <summary>voice/1 のメッセージ形（設計 §2）。フレームは Hane 連携 1.0 と同じ <c>u32 LE + UTF-8 JSON</c>（<see cref="Hane.HaneWire"/>）。本文とトークンはログに出さない。</summary>
internal static partial class VoiceProtocol
{
    public const string Version = "voice/1";
    public const int ContextBeforeScalars = 1200;
    public const int ContextAfterScalars = 200;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SessionIdRx();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex TokenRx();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex PipeNameRx();

    public static bool IsSessionId(string? s) => s is not null && SessionIdRx().IsMatch(s);
    public static bool IsToken(string? s) => s is not null && TokenRx().IsMatch(s);
    public static bool IsPipeName(string? s) => s is not null && PipeNameRx().IsMatch(s);

    // ── エンジン → Hane ──

    public static JsonObject Hello(string token, int pid) => new() { ["type"] = "hello", ["protocol"] = Version, ["token"] = token, ["pid"] = pid };

    public static string EndStatusName(EndStatus s) => s switch
    {
        EndStatus.Completed => "completed",
        EndStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    // ── Hane → エンジン ──

    /// <summary>受け取った制御メッセージ。<see cref="Type"/> は hello.ok / capture.begin / capture.stop / capture.cancel。</summary>
    public sealed record Command(string Type, string? SessionId = null, PolishContext? Context = null);

    /// <summary>検証して <see cref="Command"/> にする。形が合わない・未知の型は null（契約違反。接続を閉じる）。</summary>
    public static Command? ParseCommand(JsonObject frame)
    {
        if (frame["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type)) return null;
        switch (type)
        {
            case "hello.ok":
                return Str(frame, "protocol") == Version ? new Command(type) : null;
            case "capture.stop" or "capture.cancel":
                return Str(frame, "sessionId") is { } sid && IsSessionId(sid) ? new Command(type, sid) : null;
            case "capture.begin":
            {
                if (Str(frame, "sessionId") is not { } id || !IsSessionId(id)) return null;
                if (frame.TryGetPropertyValue("options", out var opt) && opt is not (null or JsonObject)) return null;
                PolishContext? ctx = null;
                if (frame.TryGetPropertyValue("context", out var c) && c is not null)
                {
                    if (c is not JsonObject co) return null;
                    var before = co.TryGetPropertyValue("before", out var b) ? Str(co, "before") : "";
                    var after = co.TryGetPropertyValue("after", out var a) ? Str(co, "after") : "";
                    if (before is null || after is null) return null;
                    before = Core.Hane.PolishContextBuilder.TailScalars(before, ContextBeforeScalars);
                    after = Core.Hane.PolishContextBuilder.HeadScalars(after, ContextAfterScalars);
                    var built = new PolishContext(before, after, "", [], []);
                    ctx = built.IsEmpty ? null : built;
                }
                return new Command(type, id, ctx);
            }
            default:
                return null;
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
