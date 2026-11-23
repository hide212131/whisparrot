using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whisparrot.Core.Settings;
using Whisparrot.Core.Update;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Infrastructure.Update;

namespace Whisparrot.Tests;

public sealed class SemVerTests
{
    private static SemVer V(string s) { Assert.True(SemVer.TryParse(s, out var v), s); return v; }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.4.0-preview.2", "0.4.0-preview.3")]
    [InlineData("0.4.0-preview.10", "0.4.0-preview.11")]
    [InlineData("0.3.0-preview.2", "0.4.0-preview.1")]
    [InlineData("0.2.0", "0.3.0-preview.1")]
    [InlineData("0.4.0-preview.3", "0.4.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("v0.1.3", "0.2.0")]
    public void Orders_by_semver_2_precedence(string lower, string higher)
    {
        Assert.True(V(lower) < V(higher));
        Assert.True(V(higher) > V(lower));
        Assert.Equal(-1, Math.Sign(V(lower).CompareTo(V(higher))));
    }

    [Fact]
    public void Build_metadata_and_v_prefix_are_ignored_in_comparison()
    {
        Assert.Equal(V("0.2.0"), V("v0.2.0+10524fad"));
        Assert.Equal(0, V("1.0.0+a").CompareTo(V("1.0.0+b")));
        Assert.True(V("1.0.0-rc.1+x").IsPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("1.0.0-01")]
    [InlineData("latest")]
    [InlineData("-1.0.0")]
    public void Rejects_malformed(string text) => Assert.False(SemVer.TryParse(text, out _));
}

public sealed class ReleaseSelectionTests
{
    private static readonly string Hex = new('a', 64);

    private static ReleaseAsset Asset(string name, string? digest = "sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90") => new(name, 100, "https://github.com/x/y/releases/download/t/" + name, digest);

    private static ReleaseInfo Rel(string tag, bool pre = false, bool draft = false, params ReleaseAsset[] assets) =>
        new(tag, pre, draft, assets.Length > 0 ? assets : [Asset("whisparrot-win-x64.exe"), Asset("whisparrot-win-arm64.exe")]);

    private static SemVer V(string s) { SemVer.TryParse(s, out var v); return v; }

    private static readonly ReleaseInfo[] Real =
    [
        Rel("v0.4.0-preview.3", pre: true), Rel("v0.4.0-preview.2", pre: true), Rel("v0.3.0-preview.2", pre: true),
        Rel("v0.2.0"), Rel("v0.1.3"), Rel("v0.1.0"),
    ];

    [Fact]
    public void Prerelease_channel_picks_the_highest_newer_version()
    {
        var c = ReleaseSelector.Select(Real, V("0.2.0"), includePrerelease: true, "x64");
        Assert.Equal("v0.4.0-preview.3", c!.Tag);
        Assert.True(c.Prerelease);
        Assert.Equal("whisparrot-win-x64.exe", c.AssetName);
    }

    [Fact]
    public void Stable_channel_ignores_prereleases()
    {
        Assert.Null(ReleaseSelector.Select(Real, V("0.2.0"), includePrerelease: false, "x64"));
        Assert.Equal("v0.2.0", ReleaseSelector.Select(Real, V("0.1.3"), includePrerelease: false, "x64")!.Tag);
    }

    [Fact]
    public void Prerelease_flag_or_semver_suffix_each_mark_a_prerelease()
    {
        Assert.Null(ReleaseSelector.Select([Rel("v1.0.0", pre: true)], V("0.2.0"), false, "x64"));
        Assert.Null(ReleaseSelector.Select([Rel("v1.0.0-rc.1", pre: false)], V("0.2.0"), false, "x64"));
    }

    [Theory]
    [InlineData("0.4.0-preview.3")]
    [InlineData("0.4.0")]
    [InlineData("1.0.0")]
    public void Only_strictly_newer_versions_are_candidates(string current) =>
        Assert.Null(ReleaseSelector.Select(Real, V(current), true, "x64"));

    [Fact]
    public void Prerelease_to_its_stable_is_an_update_but_not_the_reverse()
    {
        Assert.Equal("v0.4.0", ReleaseSelector.Select([Rel("v0.4.0")], V("0.4.0-preview.3"), false, "x64")!.Tag);
        Assert.Null(ReleaseSelector.Select([Rel("v0.4.0-preview.3", pre: true)], V("0.4.0"), true, "x64"));
    }

    [Fact]
    public void Drafts_and_unparsable_tags_are_skipped()
    {
        Assert.Null(ReleaseSelector.Select([Rel("v9.0.0", draft: true), Rel("nightly")], V("0.2.0"), true, "x64"));
    }

    [Theory]
    [InlineData("x64", "whisparrot-win-x64.exe")]
    [InlineData("arm64", "whisparrot-win-arm64.exe")]
    public void Picks_the_asset_for_the_architecture(string arch, string expected) =>
        Assert.Equal(expected, ReleaseSelector.Select([Rel("v1.0.0")], V("0.2.0"), true, arch)!.AssetName);

    [Fact]
    public void Design_style_asset_names_also_match_and_arches_are_not_confused()
    {
        var assets = new[] { Asset("whisparrot-v1.0.0-x64.exe"), Asset("whisparrot-v1.0.0-arm64.exe"), Asset("SHA256SUMS.txt") };
        Assert.Equal("whisparrot-v1.0.0-arm64.exe", ReleaseSelector.PickAsset(assets, "arm64")!.Name);
        Assert.Equal("whisparrot-v1.0.0-x64.exe", ReleaseSelector.PickAsset(assets, "x64")!.Name);
    }

    [Fact]
    public void No_asset_for_the_architecture_or_unsupported_arch_means_no_candidate()
    {
        var onlyX64 = Rel("v1.0.0", false, false, Asset("whisparrot-win-x64.exe"));
        Assert.Null(ReleaseSelector.Select([onlyX64], V("0.2.0"), true, "arm64"));
        Assert.Null(ReleaseSelector.Select([onlyX64], V("0.2.0"), true, "x86"));
        Assert.Null(ReleaseSelector.PickAsset([Asset("whisparrot-win-x64.exe"), Asset("whisparrot-other-x64.exe")], "x64")); // 曖昧
    }

    [Fact]
    public void A_release_without_a_usable_digest_is_not_offered_and_the_next_best_is()
    {
        var noDigest = Rel("v2.0.0", false, false, Asset("whisparrot-win-x64.exe", null));
        var badDigest = Rel("v1.5.0", false, false, Asset("whisparrot-win-x64.exe", "sha256:xyz"));
        var good = Rel("v1.0.0");
        Assert.Equal("v1.0.0", ReleaseSelector.Select([noDigest, badDigest, good], V("0.2.0"), true, "x64")!.Tag);
        Assert.Null(ReleaseSelector.Select([noDigest, badDigest], V("0.2.0"), true, "x64"));
    }

    [Fact]
    public void Parses_the_github_release_json_shape()
    {
        const string json = """
        [{"tag_name":"v0.4.0-preview.3","prerelease":true,"draft":false,"assets":[
          {"name":"whisparrot-win-x64.exe","size":77984354,"browser_download_url":"https://github.com/hide212131/whisparrot/releases/download/v0.4.0-preview.3/whisparrot-win-x64.exe",
           "digest":"sha256:f5172bc7f0b516f90813a4c7245f05588f74eb4fa4f42f51765e482312a3c52e"}]},
         {"tag_name":"v0.0.1","assets":[{"name":"a","size":1,"browser_download_url":"u","digest":null}]}]
        """;
        var list = ReleaseClient.ParseReleases(JsonDocument.Parse(json));
        var c = ReleaseSelector.Select(list, V("0.2.0"), true, "x64")!;
        Assert.Equal(77984354, c.Size);
        Assert.Equal("f5172bc7f0b516f90813a4c7245f05588f74eb4fa4f42f51765e482312a3c52e", c.Sha256);
        Assert.Null(list[1].Assets[0].Digest);
    }
}

public sealed class DigestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-digest-" + Guid.NewGuid().ToString("N"));
    public DigestTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("SHA256:" + "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("sha1:" + "0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("sha256:abc", false)]
    [InlineData("sha256:" + "g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parse(string? digest, bool ok)
    {
        Assert.Equal(ok, Sha256Digest.TryParse(digest, out var hex));
        if (ok) Assert.Equal(hex, hex.ToLowerInvariant());
    }

    private (string Path, UpdateCandidate Candidate) File_(byte[] bytes, string? hashOverride = null, long? size = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        var hex = hashOverride ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
        SemVer.TryParse("1.0.0", out var v);
        return (path, new UpdateCandidate(v, "v1.0.0", false, "whisparrot-win-x64.exe", size ?? bytes.Length, "https://github.com/x", hex));
    }

    [Fact]
    public async Task Matching_file_verifies()
    {
        var (p, c) = File_("hello"u8.ToArray());
        Assert.Null(await ReleaseClient.VerifyFileAsync(p, c));
    }

    [Fact]
    public async Task Hash_mismatch_is_reported()
    {
        var (p, c) = File_("hello"u8.ToArray(), hashOverride: new string('0', 64));
        Assert.Contains("SHA-256", await ReleaseClient.VerifyFileAsync(p, c));
    }

    [Fact]
    public async Task Size_mismatch_is_reported_before_hashing()
    {
        var (p, c) = File_("hello"u8.ToArray(), size: 99);
        Assert.Contains("サイズ", await ReleaseClient.VerifyFileAsync(p, c));
    }

    [Fact]
    public async Task Uppercase_expected_hash_still_matches()
    {
        var bytes = "hello"u8.ToArray();
        var (p, c) = File_(bytes);
        Assert.True(await Sha256Digest.MatchesAsync(p, c.Sha256.ToUpperInvariant()));
    }
}

public sealed class SwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-swap-" + Guid.NewGuid().ToString("N"));
    private readonly SwapPlan _plan;

    public SwapTests()
    {
        Directory.CreateDirectory(_dir);
        _plan = SwapPlan.For(Path.Combine(_dir, "whisparrot.exe"));
        File.WriteAllText(_plan.ExePath, "OLD");
        File.WriteAllText(_plan.NewPath, "NEW");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Exe => File.ReadAllText(_plan.ExePath);

    /// <summary>n 回目（1 始まり）の Move / Delete を失敗させる。</summary>
    private sealed class FlakyOps(int failAt) : IFileOps
    {
        private int _n;
        public List<string> Log { get; } = [];
        private void Step(string what) { Log.Add(what); if (++_n == failAt) throw new IOException("injected: " + what); }
        public bool Exists(string path) => File.Exists(path);
        public void Move(string from, string to) { Step($"move {Path.GetFileName(from)}->{Path.GetFileName(to)}"); File.Move(from, to); }
        public void Delete(string path) { Step("delete " + Path.GetFileName(path)); File.Delete(path); }
    }

    [Fact]
    public void Plan_paths_sit_next_to_the_exe()
    {
        Assert.Equal(_plan.ExePath + ".new", _plan.NewPath);
        Assert.Equal(_plan.ExePath + ".old", _plan.OldPath);
        Assert.Equal(Path.GetDirectoryName(_plan.ExePath), Path.GetDirectoryName(_plan.OldPath));
    }

    [Fact]
    public void Success_renames_old_places_new_then_launches()
    {
        var launched = "";
        var r = ExeSwapper.Apply(_plan, () => launched = Exe);
        Assert.Equal(SwapOutcome.Swapped, r.Outcome);
        Assert.Equal("NEW", launched); // 起動は入れ替え後（新 exe が本来のパスにある状態）
        Assert.Equal("NEW", Exe);
        Assert.Equal("OLD", File.ReadAllText(_plan.OldPath));
        Assert.False(File.Exists(_plan.NewPath));
    }

    [Fact]
    public void Steps_run_in_order_retire_then_place()
    {
        var ops = new FlakyOps(failAt: 99);
        ExeSwapper.Apply(_plan, () => { }, ops);
        Assert.Equal(["move whisparrot.exe->whisparrot.exe.old", "move whisparrot.exe.new->whisparrot.exe"], ops.Log);
    }

    [Fact]
    public void Failure_retiring_the_old_exe_changes_nothing()
    {
        var r = ExeSwapper.Apply(_plan, () => Assert.Fail("must not launch"), new FlakyOps(failAt: 1));
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("OLD", Exe);
        Assert.Equal("NEW", File.ReadAllText(_plan.NewPath));
        Assert.False(File.Exists(_plan.OldPath));
    }

    [Fact]
    public void Failure_placing_the_new_exe_restores_the_old_one()
    {
        var r = ExeSwapper.Apply(_plan, () => Assert.Fail("must not launch"), new FlakyOps(failAt: 2));
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("OLD", Exe);
        Assert.False(File.Exists(_plan.OldPath));
    }

    [Fact]
    public void Failure_launching_restores_the_old_exe_and_keeps_the_new_file()
    {
        var r = ExeSwapper.Apply(_plan, () => throw new InvalidOperationException("cannot start"));
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Contains("cannot start", r.Detail);
        Assert.Equal("OLD", Exe);
        Assert.Equal("NEW", File.ReadAllText(_plan.NewPath));
        Assert.False(File.Exists(_plan.OldPath));
    }

    [Fact]
    public void A_failed_rollback_reports_where_the_old_exe_is()
    {
        // 1: Exe->Old, 2: New->Exe ok, launch fails, 3: Exe->New (rollback) fails
        var r = ExeSwapper.Apply(_plan, () => throw new InvalidOperationException("x"), new FlakyOps(failAt: 3));
        Assert.Equal(SwapOutcome.RollbackFailed, r.Outcome);
        Assert.Contains(_plan.OldPath, r.Detail);
        Assert.Equal("OLD", File.ReadAllText(_plan.OldPath));
    }

    [Fact]
    public void Missing_new_file_aborts_without_touching_the_exe()
    {
        File.Delete(_plan.NewPath);
        var r = ExeSwapper.Apply(_plan, () => Assert.Fail("must not launch"));
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("OLD", Exe);
    }

    [Fact]
    public void A_stale_old_file_from_a_previous_update_is_replaced()
    {
        File.WriteAllText(_plan.OldPath, "STALE");
        Assert.Equal(SwapOutcome.Swapped, ExeSwapper.Apply(_plan, () => { }).Outcome);
        Assert.Equal("OLD", File.ReadAllText(_plan.OldPath));
    }

    [Fact]
    public void A_locked_stale_old_file_aborts_before_any_rename()
    {
        File.WriteAllText(_plan.OldPath, "STALE");
        using var lockIt = new FileStream(_plan.OldPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = ExeSwapper.Apply(_plan, () => Assert.Fail("must not launch"));
        Assert.Equal(SwapOutcome.RolledBack, r.Outcome);
        Assert.Equal("OLD", Exe);
    }

    // ---- 旧版の片付け ----

    [Fact]
    public void Cleanup_keeps_the_old_file_until_the_new_version_has_started_normally()
    {
        ExeSwapper.Apply(_plan, () => { });
        ExeSwapper.CleanOnStart(_plan, afterUpdate: true); // 再起動直後の起動では消さない
        Assert.True(File.Exists(_plan.OldPath));

        ExeSwapper.CleanOnStart(_plan, afterUpdate: false); // 正常起動の印が無い通常起動でも消さない
        Assert.True(File.Exists(_plan.OldPath));

        ExeSwapper.MarkHealthy(_plan); // 再起動直後の起動が最後まで進んだ
        ExeSwapper.CleanOnStart(_plan, afterUpdate: true);
        Assert.True(File.Exists(_plan.OldPath)); // まだ同じ起動の中

        ExeSwapper.CleanOnStart(_plan, afterUpdate: false); // 次回の通常起動
        Assert.False(File.Exists(_plan.OldPath));
        Assert.False(File.Exists(_plan.HealthyMarkerPath));
        Assert.Equal("NEW", Exe);
    }

    [Fact]
    public void Cleanup_discards_an_unapplied_new_file_and_ignores_a_missing_old_file()
    {
        ExeSwapper.CleanOnStart(_plan, afterUpdate: false);
        Assert.False(File.Exists(_plan.NewPath));
        Assert.Equal("OLD", Exe);
        ExeSwapper.MarkHealthy(_plan); // .old が無ければ印を作らない
        Assert.False(File.Exists(_plan.HealthyMarkerPath));
    }
}

public sealed class UpdateSettingsTests
{
    private static AppSettings Parse(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "whisparrot-upd-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            var r = new JsonSettingsStore(path).Load();
            Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
            return r.Settings;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Defaults_are_auto_check_on_and_prerelease_included()
    {
        Assert.True(AppSettings.Default.Updates.AutoCheck);
        Assert.True(AppSettings.Default.Updates.IncludePrerelease);
        Assert.Equal(1, AppSettings.Default.SchemaVersion);
    }

    [Fact]
    public void An_existing_config_without_the_new_keys_loads_with_defaults()
    {
        var s = Parse("""{"schema_version":1,"updates":{"last_check_attempt_utc":null,"last_check_success_utc":null}}""");
        Assert.True(s.Updates.AutoCheck);
        Assert.True(s.Updates.IncludePrerelease);
    }

    [Fact]
    public void Defaults_do_not_write_the_new_keys_so_older_builds_keep_reading_the_file()
    {
        var json = Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        Assert.DoesNotContain("auto_check", json);
        Assert.DoesNotContain("include_prerelease", json);
    }

    [Fact]
    public void Non_default_choices_round_trip()
    {
        var s = AppSettings.Default with { Updates = AppSettings.Default.Updates with { AutoCheck = false, IncludePrerelease = false } };
        var json = Encoding.UTF8.GetString(SettingsJson.Serialize(s));
        Assert.Contains("auto_check", json);
        Assert.Contains("include_prerelease", json);
        var back = Parse(json);
        Assert.False(back.Updates.AutoCheck);
        Assert.False(back.Updates.IncludePrerelease);
        Assert.Equal(1, back.SchemaVersion);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-usvc-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public UpdateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(new JsonSettingsStore(Path.Combine(_dir, "config.json")));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly ReleaseAsset X64 = new("whisparrot-win-x64.exe", 3, "https://github.com/a/b/c.exe", "sha256:" + new string('b', 64));

    private UpdateService Make(Func<CancellationToken, Task<IReadOnlyList<ReleaseInfo>>> list, Func<UpdateCandidate, string, CancellationToken, Task>? download = null, string current = "0.2.0")
    {
        SemVer.TryParse(current, out var cur);
        return new UpdateService(_settings, list, download ?? ((_, _, _) => Task.CompletedTask), cur, "x64",
            SwapPlan.For(Path.Combine(_dir, "whisparrot.exe")), () => new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
    }

    private static Task<IReadOnlyList<ReleaseInfo>> Releases(params ReleaseInfo[] r) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(r);

    [Fact]
    public async Task Network_failure_gives_up_quietly()
    {
        var svc = Make(_ => throw new HttpRequestException("offline"));
        var events = new List<bool>();
        svc.Changed += events.Add;
        await svc.CheckAsync();
        Assert.Equal(UpdateState.Idle, svc.State);
        Assert.Null(svc.Candidate);
        Assert.DoesNotContain(true, events); // 新版通知は出ない
        Assert.Null(_settings.Current.Updates.LastCheckSuccessUtc); // 成功日時は進めない
        Assert.NotNull(_settings.Current.Updates.LastCheckAttemptUtc);
    }

    [Fact]
    public async Task Finds_a_newer_version_once_and_records_success()
    {
        var svc = Make(_ => Releases(new ReleaseInfo("v0.3.0", false, false, [X64])));
        var events = new List<bool>();
        svc.Changed += events.Add;
        await svc.CheckAsync();
        Assert.Equal(UpdateState.Available, svc.State);
        Assert.Equal("v0.3.0", svc.Candidate!.Tag);
        Assert.Single(events, true);
        await svc.CheckAsync();
        Assert.Single(events, true); // 同じ版の再確認では再通知しない
        Assert.NotNull(_settings.Current.Updates.LastCheckSuccessUtc);
    }

    [Fact]
    public async Task Channel_setting_controls_the_candidate()
    {
        var pre = new ReleaseInfo("v0.3.0-preview.1", true, false, [X64]);
        var svc = Make(_ => Releases(pre));
        await svc.CheckAsync();
        Assert.Equal(UpdateState.Available, svc.State);

        _settings.Update(s => s with { Updates = s.Updates with { IncludePrerelease = false } });
        await svc.CheckAsync();
        Assert.Equal(UpdateState.Idle, svc.State);
        Assert.Null(svc.Candidate);
    }

    [Fact]
    public async Task Download_places_the_verified_file_and_becomes_ready()
    {
        string? dest = null;
        var svc = Make(_ => Releases(new ReleaseInfo("v0.3.0", false, false, [X64])), (c, d, _) => { dest = d; File.WriteAllText(d, "x"); return Task.CompletedTask; });
        await svc.CheckAsync();
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Ready, svc.State);
        Assert.Equal(svc.Plan.NewPath, dest);
    }

    [Fact]
    public async Task Digest_failure_is_surfaced_and_can_be_retried()
    {
        var fail = true;
        var svc = Make(_ => Releases(new ReleaseInfo("v0.3.0", false, false, [X64])),
            (_, _, _) => fail ? throw new UpdateException("SHA-256 が一致しません。破棄しました") : Task.CompletedTask);
        await svc.CheckAsync();
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Contains("SHA-256", svc.Message);
        fail = false;
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Ready, svc.State);
    }

    [Fact]
    public async Task Unparsable_current_version_never_offers_updates()
    {
        var svc = new UpdateService(_settings, _ => Releases(new ReleaseInfo("v9.0.0", false, false, [X64])), (_, _, _) => Task.CompletedTask,
            null, "x64", SwapPlan.For(Path.Combine(_dir, "whisparrot.exe")), () => DateTimeOffset.UtcNow);
        await svc.CheckAsync();
        Assert.Null(svc.Candidate);
    }
}
