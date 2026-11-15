using System.Security.Cryptography;
using System.Text;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

public sealed class ModelCatalogTests
{
    private const string Rev = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private static readonly string Sha = new('a', 64);

    private static string Json(string id = "base", string filename = "ggml-base.bin", string? url = null, long size = 100, string? sha = null) => $$"""
        {"schema_version":1,"models":[{"id":"{{id}}","filename":"{{filename}}",
         "url":"{{url ?? $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Rev}/{filename}"}}","size_bytes":{{size}},"sha256":"{{sha ?? Sha}}","license":"MIT"}]}
        """;

    private static ModelCatalog Parse(string json, bool all = false) => ModelCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), all);

    [Fact]
    public void Embedded_catalog_has_the_four_models_with_pinned_revision_and_sha256()
    {
        var c = ModelCatalog.Embedded();
        Assert.Equal(["base", "small", "medium", "large-v3-turbo"], ModelIds.All.Where(id => c.Get(id).Id == id));
        foreach (var e in c.Entries)
        {
            Assert.Equal("ggml-" + e.Id + ".bin", e.Filename);
            Assert.Matches("^[0-9a-f]{40}$", e.Revision);
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.DoesNotContain("/main/", e.Url.AbsolutePath);
            Assert.Equal("https", e.Url.Scheme);
            Assert.True(e.SizeBytes > 0);
        }
        Assert.Equal(Whisparrot.Infrastructure.Recognition.WhisperRecognizer.SmallModelBytes, c.Get("small").SizeBytes);
    }

    [Fact]
    public void Embedded_catalog_hashes_are_the_values_published_by_hugging_face()
    {
        // 出典: https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/<revision> の lfs.oid（sha256）と size。
        // 2026-11-15 に取得した値。再照合は build/verify-catalog.ps1。
        var expected = new Dictionary<string, (long Size, string Sha)>
        {
            ["base"] = (147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
            ["small"] = (487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
            ["medium"] = (1533763059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
            ["large-v3-turbo"] = (1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        };
        var c = ModelCatalog.Embedded();
        foreach (var (id, (size, sha)) in expected) Assert.Equal((size, sha), (c.Get(id).SizeBytes, c.Get(id).Sha256));
    }

    [Fact]
    public void Missing_models_are_rejected_when_all_are_required()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Json(), all: true));
        Assert.NotNull(Parse(Json()).Get("base"));
    }

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("base", "../ggml-base.bin")]
    [InlineData("base", "ggml-base.en.bin")]
    [InlineData("base", "ggml-base.exe")]
    public void Unknown_ids_and_unsafe_or_english_only_filenames_are_rejected(string id, string filename)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Json(id, filename)));
    }

    [Theory]
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin")]
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861/ggml-base.bin")]
    [InlineData("http://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin")]
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin")]
    public void Floating_short_insecure_or_mismatched_urls_are_rejected(string url)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Json(url: url)));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(100, "ABCDEF")]
    [InlineData(100, "0000000000000000000000000000000000000000000000000000000000000000z")]
    public void Zero_size_and_malformed_hashes_are_rejected(long size, string? sha)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Json(size: size, sha: sha)));
    }

    [Fact]
    public void Uppercase_hashes_are_rejected_so_comparison_stays_exact()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Json(sha: new string('A', 64))));
    }
}

public sealed class ModelManagerTests : IDisposable
{
    private const string Rev = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-models-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    private readonly byte[] _payload = MakePayload(1_500_000, 7);

    public ModelManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] MakePayload(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private TestTlsServer Server(Func<TestRequest, TestResponse> handler)
    {
        var s = new TestTlsServer(handler);
        _disposables.Add(s);
        return s;
    }

    private static ModelCatalog Catalog(string host, byte[] payload, string id = "base") =>
        ModelCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {"schema_version":1,"models":[{"id":"{{id}}","filename":"ggml-{{id}}.bin",
             "url":"https://{{host}}/ggerganov/whisper.cpp/resolve/{{Rev}}/ggml-{{id}}.bin",
             "size_bytes":{{payload.Length}},"sha256":"{{Sha(payload)}}","license":"MIT"}]}
            """)), requireAllModels: false);

    private ModelManager Manager(TestTlsServer server, byte[]? payload = null, Func<string, bool>? inUse = null, Func<long>? free = null, string host = "models.test", TestConnectProxy? proxy = null)
    {
        var p = proxy ?? new TestConnectProxy();
        if (proxy is null) _disposables.Add(p);
        p.Map[host] = server.Port;
        var net = new NetworkContext(() => ProxyConfig.Environment,
            ProxyEnvironment.From([KeyValuePair.Create<string, string?>("HTTPS_PROXY", p.Url)]), server.TrustThisServer);
        return new ModelManager(_dir, Catalog(host, payload ?? _payload), net, inUse, free);
    }

    private string Model => Path.Combine(_dir, "ggml-base.bin");
    private string Part => Model + ".part";

    private static TestResponse Ok(byte[] body) => new(200, body);

    [Fact]
    public async Task Download_verifies_the_hash_and_publishes_atomically_through_the_proxy()
    {
        var server = Server(_ => Ok(_payload));
        var proxy = new TestConnectProxy(); _disposables.Add(proxy);
        var m = Manager(server, proxy: proxy);
        var reports = new List<DownloadProgress>();
        await m.DownloadAsync("base", new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(_payload, File.ReadAllBytes(Model));
        Assert.False(File.Exists(Part));
        Assert.Equal(["models.test"], proxy.Tunnelled.Select(c => c.Host).Distinct()); // 取得は NetworkContext（プロキシ）経由
        Assert.Equal(ModelState.Ready, (await m.GetStatusAsync("base")).State);
        Assert.Null(m.CheckReady("base"));
        Assert.Equal(_payload.Length, reports[^1].BytesDone);
        Assert.Equal(_payload.Length, reports[^1].BytesTotal);
    }

    private sealed class SyncProgress(Action<DownloadProgress> a) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => a(value);
    }

    [Fact]
    public async Task Hash_mismatch_with_the_right_size_is_discarded_and_nothing_is_published()
    {
        var tampered = (byte[])_payload.Clone();
        tampered[1000] ^= 0xFF;
        var server = Server(_ => Ok(tampered));
        var m = Manager(server);
        var ex = await Assert.ThrowsAsync<ModelException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(Model));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task Connection_cut_midway_leaves_no_model_and_no_part_file()
    {
        // Content-Length は宣言どおりだが、本文が途中で切れる。
        var server = Server(_ => new TestResponse(200, _payload[..500_000], ContentLength: _payload.Length));
        var m = Manager(server);
        await Assert.ThrowsAnyAsync<Exception>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.False(File.Exists(Model));
        Assert.False(File.Exists(Part));
        Assert.NotNull(m.CheckReady("base")); // 未取得のまま使えない
    }

    [Fact]
    public async Task Size_mismatch_announced_by_the_server_is_refused_before_writing()
    {
        var server = Server(_ => Ok(_payload.Append((byte)1).ToArray()));
        var m = Manager(server);
        var ex = await Assert.ThrowsAsync<ModelException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Contains("サイズ", ex.Message);
        Assert.False(File.Exists(Model));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task Http_errors_are_failures_that_leave_nothing_behind()
    {
        var server = Server(_ => new TestResponse(503, []));
        var m = Manager(server);
        var ex = await Assert.ThrowsAsync<ModelException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Contains("503", ex.Message);
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task Cancelling_midway_removes_the_part_file_and_keeps_an_existing_model()
    {
        File.WriteAllBytes(Model, [1, 2, 3]); // 壊れた既存（サイズ違い）。置き換え完了までそのまま
        var release = new TaskCompletionSource();
        var server = Server(_ => new TestResponse(200, _payload, HoldAfter: 400_000, Release: release.Task));
        var m = Manager(server);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var task = m.DownloadAsync("base", new SyncProgress(p => { if (p.BytesDone > 0) started.TrySetResult(); }), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(File.Exists(Part));
        Assert.Equal(ModelState.Downloading, (await m.GetStatusAsync("base")).State);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        release.TrySetResult();
        Assert.False(File.Exists(Part));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Model));
        Assert.Equal(ModelState.Corrupt, (await m.GetStatusAsync("base")).State);
    }

    [Fact]
    public async Task A_verified_existing_model_is_kept_and_not_downloaded_again()
    {
        File.WriteAllBytes(Model, _payload);
        var server = Server(_ => Ok(MakePayload(_payload.Length, 99)));
        var m = Manager(server);
        var before = File.GetLastWriteTimeUtc(Model);
        await m.DownloadAsync("base", null, CancellationToken.None);
        Assert.Empty(server.Requests);
        Assert.Equal(_payload, File.ReadAllBytes(Model));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Model));
    }

    [Fact]
    public async Task A_corrupt_existing_model_survives_a_failed_redownload_and_is_replaced_after_a_good_one()
    {
        var corrupt = (byte[])_payload.Clone();
        corrupt[10] ^= 1;
        File.WriteAllBytes(Model, corrupt);
        var fail = true;
        var server = Server(_ => fail ? new TestResponse(500, []) : Ok(_payload));
        var m = Manager(server);
        Assert.Equal(ModelState.Corrupt, (await m.GetStatusAsync("base")).State);
        Assert.Contains("壊れて", m.CheckReady("base"));

        await Assert.ThrowsAsync<ModelException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Equal(corrupt, File.ReadAllBytes(Model)); // 既存はそのまま

        fail = false;
        await m.DownloadAsync("base", null, CancellationToken.None);
        Assert.Equal(_payload, File.ReadAllBytes(Model));
        Assert.Null(m.CheckReady("base"));
    }

    [Fact]
    public async Task A_model_that_is_loaded_is_never_replaced()
    {
        var corrupt = (byte[])_payload.Clone();
        corrupt[10] ^= 1;
        File.WriteAllBytes(Model, corrupt);
        var server = Server(_ => Ok(_payload));
        var m = Manager(server, inUse: _ => true);
        await Assert.ThrowsAsync<ModelInUseException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Empty(server.Requests);
        Assert.Equal(corrupt, File.ReadAllBytes(Model));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task Replacing_a_file_held_open_by_another_process_fails_without_losing_it()
    {
        var corrupt = (byte[])_payload.Clone();
        corrupt[10] ^= 1;
        File.WriteAllBytes(Model, corrupt);
        var server = Server(_ => Ok(_payload));
        var m = Manager(server);
        using (new FileStream(Model, FileMode.Open, FileAccess.Read, FileShare.Read)) // whisper.cpp が読み込み中を想定
        {
            await Assert.ThrowsAsync<ModelInUseException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        }
        Assert.Equal(corrupt, File.ReadAllBytes(Model));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task A_second_download_while_one_is_running_is_refused()
    {
        var release = new TaskCompletionSource();
        var server = Server(_ => new TestResponse(200, _payload, HoldAfter: 100_000, Release: release.Task));
        var m = Manager(server);
        var started = new TaskCompletionSource();
        var first = m.DownloadAsync("base", new SyncProgress(p => { if (p.BytesDone > 0) started.TrySetResult(); }), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<ModelBusyException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        release.SetResult();
        await first;
        Assert.Equal(_payload, File.ReadAllBytes(Model));
    }

    [Fact]
    public async Task Insufficient_free_space_is_reported_before_downloading()
    {
        var server = Server(_ => Ok(_payload));
        var m = Manager(server, free: () => 1000);
        var ex = await Assert.ThrowsAsync<ModelException>(() => m.DownloadAsync("base", null, CancellationToken.None));
        Assert.Contains("空き容量", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Verification_is_cached_but_a_changed_file_is_re_verified()
    {
        File.WriteAllBytes(Model, _payload);
        var m = Manager(Server(_ => Ok(_payload)));
        Assert.Equal(ModelState.Ready, (await m.GetStatusAsync("base")).State);

        var changed = (byte[])_payload.Clone();
        changed[5] ^= 1; // 同サイズで中身だけ変更
        File.WriteAllBytes(Model, changed);
        File.SetLastWriteTimeUtc(Model, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(ModelState.Corrupt, (await m.GetStatusAsync("base")).State);
        Assert.Contains("壊れて", m.CheckReady("base"));
    }

    [Fact]
    public async Task Wrong_size_files_and_part_files_never_count_as_a_usable_model()
    {
        var m = Manager(Server(_ => Ok(_payload)));
        File.WriteAllBytes(Part, _payload); // 完全な内容でも .part は使わない
        Assert.Equal(ModelState.NotDownloaded, (await m.GetStatusAsync("base")).State);
        Assert.Contains("未取得", m.CheckReady("base"));
        File.WriteAllBytes(Model, _payload[..100]);
        Assert.Equal(ModelState.Corrupt, (await m.GetStatusAsync("base")).State);
        Assert.NotNull(m.CheckReady("base"));
    }

    [Fact]
    public async Task Recognizer_resolver_target_reflects_readiness_so_unverified_files_never_reach_the_worker()
    {
        var m = Manager(Server(_ => Ok(_payload)));
        var recognizer = new WhisperRecognizer(new WhisperRecognizerOptions("", ["cpu"], new HashSet<string> { "cpu" },
            ModelResolver: () => new ModelTarget(m.PathOf("base"), m.CheckReady("base"))));
        Assert.Contains("未取得", recognizer.CheckReady());
        await Assert.ThrowsAsync<Whisparrot.Core.RecognitionException>(() =>
            recognizer.RecognizeAsync(new Whisparrot.Core.RecognitionRequest(new Whisparrot.Core.PcmAudio(new float[16000])), CancellationToken.None));
        File.WriteAllBytes(Model, _payload);
        Assert.Null(recognizer.CheckReady());
        recognizer.Dispose();
    }
}
