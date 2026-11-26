using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparrot.Infrastructure.Voice;

namespace Whisparrot.Tests.Voice;

/// <summary>常駐アプリの自己登録（検出用）。新規作成・上書き・原子性・エンジンモードで書かないこと、hello の確定形。</summary>
public sealed class EngineRegistrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wp-reg-").FullName;
    private string RegPath => Path.Combine(_dir, "whisparrot", EngineRegistration.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whisparrot.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private JsonObject Read() => JsonNode.Parse(File.ReadAllText(RegPath))!.AsObject();

    [Fact]
    public void Creates_the_registration_with_absolute_path_version_engine_and_timestamp()
    {
        var exe = Path.Combine(_dir, "apps", "whisparrot.exe");
        Assert.True(EngineRegistration.TryWrite(exe, "1.2.3", new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(9)), RegPath));

        var j = Read();
        Assert.Equal(Path.GetFullPath(exe), j["exePath"]!.GetValue<string>());
        Assert.True(Path.IsPathRooted(j["exePath"]!.GetValue<string>()));
        Assert.Equal("1.2.3", j["version"]!.GetValue<string>());
        Assert.True(j["engine"]!.GetValue<bool>());
        Assert.Equal("2026-11-26T01:00:00Z", j["updatedAt"]!.GetValue<string>());
        Assert.Equal(4, j.Count);
    }

    [Fact]
    public void Overwrites_an_existing_registration_with_the_new_path_and_version()
    {
        var t = DateTimeOffset.UtcNow;
        EngineRegistration.Write(RegPath, Path.Combine(_dir, "old", "whisparrot.exe"), "1.0.0", t);
        EngineRegistration.Write(RegPath, Path.Combine(_dir, "new", "whisparrot.exe"), "1.1.0", t.AddMinutes(5));

        var j = Read();
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "new", "whisparrot.exe")), j["exePath"]!.GetValue<string>());
        Assert.Equal("1.1.0", j["version"]!.GetValue<string>());
    }

    [Fact]
    public void Leaves_no_temporary_files_after_writing()
    {
        EngineRegistration.Write(RegPath, Path.Combine(_dir, "whisparrot.exe"), "1.0.0", DateTimeOffset.UtcNow);
        EngineRegistration.Write(RegPath, Path.Combine(_dir, "whisparrot.exe"), "1.0.1", DateTimeOffset.UtcNow);

        Assert.Equal([EngineRegistration.FileName], Directory.GetFiles(Path.GetDirectoryName(RegPath)!).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void A_failed_write_keeps_the_previous_registration_and_does_not_throw()
    {
        EngineRegistration.Write(RegPath, Path.Combine(_dir, "whisparrot.exe"), "1.0.0", DateTimeOffset.UtcNow);
        // 親ディレクトリのつもりの場所が通常ファイルなので、書き込みは失敗する。
        var blocked = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocked, "x");

        Assert.False(EngineRegistration.TryWrite(Path.Combine(_dir, "whisparrot.exe"), "9.9.9", DateTimeOffset.UtcNow, Path.Combine(blocked, EngineRegistration.FileName)));
        Assert.Equal("1.0.0", Read()["version"]!.GetValue<string>());
    }

    [Fact]
    public void The_default_project_version_is_not_older_than_the_minimum_the_consumer_accepts()
    {
        // 版付けなしの publish でも、登録される版（3要素アセンブリ版）が古すぎると判定されないこと。
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var declared = System.Text.RegularExpressions.Regex.Match(props, @"<Version>(\d+\.\d+\.\d+)</Version>").Groups[1].Value;
        Assert.True(Version.TryParse(declared, out var v), "Directory.Build.props の Version が3要素で読めること");
        Assert.True(v >= new Version(0, 4, 0), $"既定の Version {declared} は連携側の下限 0.4.0 以上であること");

        // 実際にビルドされたアセンブリの版も宣言と一致し（版付けなしのテストビルド）、登録に書かれる形（ToString(3)）になる。
        var built = typeof(EngineRegistration).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(declared, built);

        EngineRegistration.Write(RegPath, Path.Combine(_dir, "whisparrot.exe"), built, DateTimeOffset.UtcNow);
        Assert.Equal(declared, Read()["version"]!.GetValue<string>());
    }

    [Fact]
    public void Default_location_is_under_local_app_data_whisparrot()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisparrot", "engine-registration.json");
        Assert.Equal(expected, EngineRegistration.DefaultPath);
    }

    [Fact]
    public void Only_the_resident_path_registers_and_the_engine_mode_does_not()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Whisparrot.App", "Program.cs"));
        var engine = program.IndexOf("VoiceEngineCommand.Flag", StringComparison.Ordinal);
        var reg = program.IndexOf("EngineRegistration.TryWrite(", StringComparison.Ordinal);
        Assert.True(engine >= 0 && reg > engine, "登録はエンジンモードの分岐より後（常駐側）にだけある");
        Assert.True(reg > program.IndexOf("new Mutex(", StringComparison.Ordinal), "二重起動で終了するプロセスは登録しない");
        Assert.True(reg < program.IndexOf("TrayApp.Run(", StringComparison.Ordinal));
        Assert.Single(program.Split("EngineRegistration.").Skip(1));

        var cmd = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Whisparrot.App", "VoiceEngineCommand.cs"));
        Assert.DoesNotContain("EngineRegistration", cmd);
    }

    [Fact]
    public void Hello_has_the_final_shape_with_string_token_and_numeric_pid()
    {
        var token = new string('a', 64);
        var hello = VoiceProtocol.Hello(token, 4321);

        Assert.Equal(4, hello.Count);
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.Equal("voice/1", hello["protocol"]!.GetValue<string>());
        Assert.Equal(token, hello["token"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, hello["pid"]!.GetValueKind());
        Assert.Equal(4321, hello["pid"]!.GetValue<int>());
    }

    [Fact]
    public void Hello_ok_is_accepted_only_with_protocol_voice_1()
    {
        Assert.Equal("hello.ok", VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "hello.ok", ["protocol"] = "voice/1" })?.Type);
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "hello.ok" }));
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "hello.ok", ["protocol"] = 1 }));
    }
}
