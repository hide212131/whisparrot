using System.Buffers.Binary;
using System.Text;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Tests.Fixtures;

namespace Whisparrot.Tests;

/// <summary>設計 §6 の WAV 変換（純粋関数）の試験。</summary>
public sealed class WavEncoderTests
{
    [Fact]
    public void Header_is_a_44_byte_canonical_pcm16_mono_16k_wav()
    {
        var wav = WavEncoder.Encode(new float[160]);
        Assert.Equal(44 + 320, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal((uint)(wav.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));      // mono
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(320u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
    }

    [Theory]
    [InlineData(10, 320_044)]
    [InlineData(30, 960_044)]
    public void Ten_and_thirty_second_recordings_have_the_documented_size(int seconds, int bytes) =>
        Assert.Equal(bytes, WavEncoder.EncodedLength(seconds * PcmAudio.SampleRate));

    [Fact]
    public void Samples_convert_with_asymmetric_scale_clamp_and_saturation()
    {
        var wav = WavEncoder.Encode([0f, 1f, -1f, 0.5f, -0.5f, 2f, -2f, 1e9f, float.MaxValue, float.MinValue]);
        short At(int i) => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + 2 * i));
        Assert.Equal(0, At(0));
        Assert.Equal(32767, At(1));
        Assert.Equal(-32768, At(2));
        Assert.Equal(16384, At(3));    // 0.5 * 32767 = 16383.5 → 0 から遠ざける
        Assert.Equal(-16384, At(4));
        Assert.Equal(32767, At(5));
        Assert.Equal(-32768, At(6));
        Assert.Equal(32767, At(7));
        Assert.Equal(32767, At(8));
        Assert.Equal(-32768, At(9));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Non_finite_samples_are_rejected(float bad) =>
        Assert.Throws<NonFiniteSampleException>(() => WavEncoder.Encode([0f, bad, 0f]));

    [Fact]
    public void The_limit_is_exactly_25_000_000_bytes_for_the_whole_file()
    {
        Assert.Equal(12_499_978, WavEncoder.MaxSamples);
        Assert.Equal(25_000_000, WavEncoder.EncodedLength(WavEncoder.MaxSamples));
        Assert.True(WavEncoder.FitsLimit(WavEncoder.MaxSamples));
        Assert.False(WavEncoder.FitsLimit(WavEncoder.MaxSamples + 1));
        Assert.Equal(25_000_002, WavEncoder.EncodedLength(WavEncoder.MaxSamples + 1));
        Assert.False(WavEncoder.FitsLimit(-1));
        Assert.False(WavEncoder.FitsLimit(long.MaxValue));
        Assert.Throws<OverflowException>(() => WavEncoder.EncodedLength(long.MaxValue)); // 長さ計算は checked
    }

    [Fact]
    public void Exactly_the_maximum_encodes_and_one_more_is_refused_before_allocating()
    {
        var max = WavEncoder.Encode(new float[WavEncoder.MaxSamples]);
        Assert.Equal(25_000_000, max.Length);
        Assert.Equal(24_999_956u, BinaryPrimitives.ReadUInt32LittleEndian(max.AsSpan(40)));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavEncoder.Encode(new float[WavEncoder.MaxSamples + 1]));
    }

    [Fact]
    public void A_cancelled_token_stops_the_encode_loop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => WavEncoder.Encode(new float[1000], cts.Token));
    }

    [Fact]
    public void The_tail_of_the_recording_is_present_in_the_wav()
    {
        var samples = new float[1000];
        samples[^1] = 0.25f;
        var wav = WavEncoder.Encode(samples);
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(wav.Length - 2)));  // 0.25 * 32767 = 8191.75
    }
}

/// <summary>設計 §7 の CloudTranscriber を、ループバックのモックサーバーへの実 HTTP で試験する（実在のサービスへは接続しない）。</summary>
public sealed class CloudTranscriberTests : IDisposable
{
    private const string DummyKey = "dummy-key-NOT-A-REAL-SECRET-0123456789";
    private const string EchoSecret = "echo-body-SECRET-from-server";

    private readonly List<IDisposable> _disposables = [];
    private readonly ManualResetEventSlim _release = new(false);

    public void Dispose()
    {
        _release.Set();
        foreach (var d in _disposables) d.Dispose();
    }

    private TestHttpServer Server(Func<TestRequest, TestResponse>? handler = null)
    {
        var s = new TestHttpServer(handler ?? (_ => Ok("こんにちは")));
        _disposables.Add(s);
        return s;
    }

    private static TestResponse Ok(string text) => TestHttpServer.Json(200, new { text });

    private static NetworkContext Direct() => new(() => ProxyConfig.Environment, ProxyEnvironment.Empty);

    private static CloudTranscriber Transcriber(string baseUrl, string? key = DummyKey, string model = "my-whisper", double seconds = 60, NetworkContext? net = null, string language = "ja") =>
        new(net ?? Direct(), new Uri(baseUrl.TrimEnd('/') + CloudRecognitionConfig.TranscriptionPath), model, key, TimeSpan.FromSeconds(seconds), language);

    private static PcmAudio Audio(int samples = 16000) => new(Enumerable.Range(0, samples).Select(i => (float)Math.Sin(i / 20.0) * 0.5f).ToArray());

    // ---- multipart ----

    private sealed record Part(string Disposition, string? ContentType, byte[] Data);

    private static List<Part> ParseMultipart(TestRequest req)
    {
        var ct = req.Headers["Content-Type"];
        Assert.StartsWith("multipart/form-data", ct);
        var boundary = ct[(ct.IndexOf("boundary=", StringComparison.Ordinal) + 9)..].Trim('"');
        var text = Encoding.Latin1.GetString(req.Body!);   // バイナリ安全な 1:1 変換
        var parts = new List<Part>();
        foreach (var raw in text.Split("--" + boundary).Skip(1))
        {
            if (raw.StartsWith("--")) break;
            var body = raw[2..];  // 先頭の CRLF
            var split = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headers = body[..split].Split("\r\n");
            var data = body[(split + 4)..];
            data = data[..^2];    // 末尾の CRLF
            parts.Add(new Part(
                headers.Single(h => h.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase)),
                headers.SingleOrDefault(h => h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)),
                Encoding.Latin1.GetBytes(data)));
        }
        return parts;
    }

    [Fact]
    public async Task Request_is_a_multipart_post_with_file_model_and_the_default_language()
    {
        var server = Server();
        var pcm = Audio();
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(pcm, CancellationToken.None);

        Assert.True(r.IsSuccess);
        Assert.Equal("こんにちは", r.Text);
        var req = Assert.Single(server.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/audio/transcriptions", req.Target);
        Assert.Equal("Bearer " + DummyKey, req.Headers["Authorization"]);

        var parts = ParseMultipart(req);
        Assert.Equal(3, parts.Count);
        var file = parts.Single(p => p.Disposition.Contains("name=file"));
        Assert.Contains("filename=speech.wav", file.Disposition);
        Assert.Equal("Content-Type: audio/wav", file.ContentType);
        Assert.Equal(WavEncoder.Encode(pcm.Samples), file.Data);   // PCM16 WAV そのもの（再リサンプルなし）

        var model = parts.Single(p => p.Disposition.Contains("name=model"));
        Assert.Equal("my-whisper", Encoding.UTF8.GetString(model.Data));
        Assert.Null(model.ContentType);
        var language = parts.Single(p => p.Disposition.Contains("name=language"));
        Assert.Equal("ja", Encoding.UTF8.GetString(language.Data));
        Assert.Null(language.ContentType);
        // response_format / temperature / prompt / stream などは送らない
        foreach (var banned in new[] { "response_format", "temperature", "prompt", "stream", "timestamp" })
            Assert.DoesNotContain($"name={banned}", string.Join('\n', parts.Select(p => p.Disposition)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public async Task A_configured_language_code_is_sent_as_entered(string code)
    {
        var server = Server();
        await Transcriber(server.BaseUrl, language: code).TranscribeAsync(Audio(), CancellationToken.None);
        var parts = ParseMultipart(Assert.Single(server.Requests));
        Assert.Equal(code, Encoding.UTF8.GetString(parts.Single(p => p.Disposition.Contains("name=language")).Data));
    }

    [Fact]
    public async Task An_empty_language_code_omits_the_language_field_entirely()
    {
        var server = Server();
        await Transcriber(server.BaseUrl, language: "").TranscribeAsync(Audio(), CancellationToken.None);
        var parts = ParseMultipart(Assert.Single(server.Requests));
        Assert.Equal(2, parts.Count);
        Assert.DoesNotContain("name=language", string.Join('\n', parts.Select(p => p.Disposition)));
    }

    [Fact]
    public async Task The_model_name_is_sent_exactly_as_entered()
    {
        var server = Server();
        await Transcriber(server.BaseUrl, model: "Azure/Whisper-1 日本語").TranscribeAsync(Audio(), CancellationToken.None);
        var model = ParseMultipart(Assert.Single(server.Requests)).Single(p => p.Disposition.Contains("name=model"));
        Assert.Equal("Azure/Whisper-1 日本語", Encoding.UTF8.GetString(model.Data));
    }

    [Fact]
    public async Task The_key_is_only_in_the_Authorization_header_and_a_missing_key_sends_none()
    {
        var server = Server();
        await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        var withKey = Assert.Single(server.Requests);
        Assert.DoesNotContain(DummyKey, withKey.Target);
        Assert.Equal(1, withKey.Headers.Count(h => h.Value.Contains(DummyKey)));
        Assert.DoesNotContain(DummyKey, Encoding.Latin1.GetString(withKey.Body!));

        var server2 = Server();
        await Transcriber(server2.BaseUrl, key: null).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.False(Assert.Single(server2.Requests).Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("/v1", "/v1/audio/transcriptions")]
    [InlineData("/v1/", "/v1/audio/transcriptions")]
    [InlineData("/gateway/v1/", "/gateway/v1/audio/transcriptions")]
    public async Task The_endpoint_is_the_base_path_plus_audio_transcriptions(string basePath, string expected)
    {
        var server = Server();
        var ready = (CloudRecognitionConfig.Ready)CloudRecognitionConfig.Prepare(
            new Whisparrot.Core.Settings.RecognitionSettings("small", "ja", Whisparrot.Core.Settings.RecognitionEngine.Cloud,
                new($"http://127.0.0.1:{server.Port}{basePath}", null, "m")), new Whisparrot.Infrastructure.Settings.DpapiSecretProtector());
        await new CloudTranscriber(Direct(), ready.Endpoint, ready.Model, ready.ApiKey).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(expected, Assert.Single(server.Requests).Target);
    }

    // ---- 応答 ----

    [Fact]
    public async Task Extra_fields_are_ignored_and_the_text_is_trimmed()
    {
        var server = Server(_ => TestHttpServer.Json(200, new { text = "  明日 は 会議です  ", language = "ja", duration = 1.5, segments = new[] { 1, 2 } }));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal("明日 は 会議です", r.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task An_empty_text_is_a_success_with_empty_text_not_an_error(string text)
    {
        var server = Server(_ => Ok(text));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.True(r.IsSuccess);
        Assert.Equal("", r.Text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":123}")]
    [InlineData("{\"text\":[\"a\"]}")]
    [InlineData("[\"text\"]")]
    [InlineData("\"text\"")]
    [InlineData("not json")]
    [InlineData("{\"text\":\"cut off")]
    [InlineData("")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"x\"}}]}")]
    public async Task Anything_but_a_string_text_is_malformed(string body)
    {
        var server = Server(_ => new(200, Encoding.UTF8.GetBytes(body), new() { ["Content-Type"] = "application/json" }));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Equal(TranscriptionFailure.Malformed, r.Failure);
        Assert.Equal("文字起こしの応答を解釈できませんでした", r.Message);
    }

    [Fact]
    public async Task The_response_limit_is_one_MiB_by_the_actual_bytes()
    {
        // text を埋めて、本文全体をちょうど 1 MiB / 1 MiB + 1 にする。
        static byte[] Body(int total)
        {
            const string head = "{\"text\":\"", tail = "\"}";
            return Encoding.ASCII.GetBytes(head + new string('a', total - head.Length - tail.Length) + tail);
        }
        var server = Server(_ => new(200, Body(1024 * 1024)));
        var ok = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.True(ok.IsSuccess);
        Assert.Equal(1024 * 1024 - 11, ok.Text!.Length);

        var over = Server(_ => new(200, Body(1024 * 1024 + 1)));
        var r = await Transcriber(over.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.ResponseTooLarge, r.Failure);
    }

    [Fact]
    public async Task A_declared_oversize_length_is_refused_without_reading_the_body()
    {
        var server = Server(_ => new(200, Encoding.ASCII.GetBytes("{\"text\":\"x\"}"), ContentLength: 5 * 1024 * 1024));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.ResponseTooLarge, r.Failure);
    }

    [Fact]
    public async Task A_body_shorter_than_its_declared_length_is_a_network_failure()
    {
        var server = Server(_ => new(200, Encoding.ASCII.GetBytes("{\"text\":\"x"), ContentLength: 100));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Failure, new[] { TranscriptionFailure.Network, TranscriptionFailure.Malformed });
    }

    // ---- 失敗の分類・再送なし・秘密の非露出 ----

    [Theory]
    [InlineData(401, TranscriptionFailure.Auth)]
    [InlineData(403, TranscriptionFailure.Auth)]
    [InlineData(404, TranscriptionFailure.Http)]
    [InlineData(413, TranscriptionFailure.PayloadTooLarge)]
    [InlineData(429, TranscriptionFailure.Http)]
    [InlineData(500, TranscriptionFailure.Http)]
    [InlineData(503, TranscriptionFailure.Http)]
    [InlineData(400, TranscriptionFailure.Http)]
    public async Task Http_errors_are_classified_never_retried_and_never_echo_the_server_body(int status, TranscriptionFailure expected)
    {
        var server = Server(req => TestHttpServer.Json(status, new { error = new { message = $"{EchoSecret} {DummyKey}" } })
            with { Headers = new() { ["Content-Type"] = "application/json", ["Retry-After"] = "1" } });
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);

        Assert.False(r.IsSuccess);
        Assert.Equal(expected, r.Failure);
        Assert.Single(server.Requests);   // 自動再送 0（429 の Retry-After でも待たない）
        Assert.Equal(status, r.HttpStatus);
        foreach (var s in new[] { r.Message, r.Detail ?? "", r.ToString() })
        {
            Assert.DoesNotContain(EchoSecret, s);
            Assert.DoesNotContain(DummyKey, s);
        }
        if (expected == TranscriptionFailure.Http) Assert.Contains($"HTTP {status}", r.Message);
        if (expected == TranscriptionFailure.Auth) Assert.Contains("認証エラー", r.Message);
        if (expected == TranscriptionFailure.PayloadTooLarge) Assert.Contains("上限", r.Message);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_are_not_followed(int status)
    {
        var target = Server(_ => Ok("redirected"));
        var server = Server(_ => TestResponse.Redirect($"http://127.0.0.1:{target.Port}/v1/audio/transcriptions", status));
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.Http, r.Failure);
        Assert.Equal(status, r.HttpStatus);
        Assert.Empty(target.Requests);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_refused_connection_is_a_network_failure_with_one_attempt()
    {
        int port;
        using (var s = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { s.Start(); port = ((System.Net.IPEndPoint)s.LocalEndpoint).Port; }
        var r = await Transcriber($"http://127.0.0.1:{port}/v1").TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.Network, r.Failure);
        Assert.Contains("接続エラー", r.Message);
    }

    [Fact]
    public async Task A_dropped_connection_is_a_network_failure()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _disposables.Add(new Disposer(listener.Stop));
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () => { using var c = await listener.AcceptTcpClientAsync(); /* 何も返さず閉じる */ });
        var r = await Transcriber($"http://127.0.0.1:{port}/v1").TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.Network, r.Failure);
    }

    [Fact]
    public async Task A_non_loopback_http_url_is_refused_by_the_network_layer_without_connecting()
    {
        var r = await Transcriber("http://transcription.example.invalid/v1").TranscribeAsync(Audio(), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.Network, r.Failure);
    }

    // ---- 音声の上限・異常 ----

    [Fact]
    public async Task A_recording_over_the_limit_sends_nothing()
    {
        var server = Server();
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(new PcmAudio(new float[WavEncoder.MaxSamples + 1]), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.AudioTooLong, r.Failure);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_recording_at_exactly_the_limit_is_sent()
    {
        var server = Server();
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(new PcmAudio(new float[WavEncoder.MaxSamples]), CancellationToken.None);
        Assert.True(r.IsSuccess);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Non_finite_audio_fails_before_any_request()
    {
        var server = Server();
        var r = await Transcriber(server.BaseUrl).TranscribeAsync(new PcmAudio([0f, float.NaN]), CancellationToken.None);
        Assert.Equal(TranscriptionFailure.InvalidAudio, r.Failure);
        Assert.Empty(server.Requests);
    }

    // ---- 期限・取消 ----

    [Fact]
    public async Task A_server_that_never_answers_ends_at_the_deadline_without_retry()
    {
        var server = Server(_ => { _release.Wait(20000); return Ok("late"); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Transcriber(server.BaseUrl, seconds: 0.6).TranscribeAsync(Audio(), CancellationToken.None);
        sw.Stop();
        Assert.Equal(TranscriptionFailure.Timeout, r.Failure);
        Assert.Contains("時間切れ", r.Message);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 5);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_ends_at_the_same_deadline()
    {
        var body = Encoding.ASCII.GetBytes("{\"text\":\"abcdefghij\"}");
        var gate = new TaskCompletionSource();
        _disposables.Add(new Disposer(() => gate.TrySetResult()));
        var server = Server(_ => new(200, body, HoldAfter: 5, Release: gate.Task));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Transcriber(server.BaseUrl, seconds: 0.6).TranscribeAsync(Audio(), CancellationToken.None);
        sw.Stop();
        Assert.Equal(TranscriptionFailure.Timeout, r.Failure);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 5);
    }

    [Fact]
    public async Task User_cancellation_throws_and_is_not_a_failure_classification()
    {
        var server = Server(_ => { _release.Wait(20000); return Ok("late"); });
        using var cts = new CancellationTokenSource();
        var task = Transcriber(server.BaseUrl).TranscribeAsync(Audio(), cts.Token);
        await Wait.UntilAsync(() => server.Requests.Count == 1, "request arrived");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RecognizeAsync_maps_failures_to_RecognitionException_with_the_fixed_message_and_reports_cloud()
    {
        var ok = Server();
        var result = await new CloudTranscriber(Direct(), new Uri(ok.BaseUrl + "/audio/transcriptions"), "m", null).RecognizeAsync(new RecognitionRequest(Audio()), CancellationToken.None);
        Assert.Equal(new RecognitionResult("こんにちは", "cloud"), result);

        var bad = Server(_ => TestHttpServer.Json(401, new { error = EchoSecret }));
        var ex = await Assert.ThrowsAsync<RecognitionException>(() =>
            new CloudTranscriber(Direct(), new Uri(bad.BaseUrl + "/audio/transcriptions"), "m", null).RecognizeAsync(new RecognitionRequest(Audio()), CancellationToken.None));
        Assert.Contains("認証エラー", ex.Message);
        Assert.DoesNotContain(EchoSecret, ex.Message);
    }

    [Fact]
    public async Task Fifty_sequential_requests_leave_no_pending_work()
    {
        var server = Server();
        var t = Transcriber(server.BaseUrl);
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 50; i++) Assert.True((await t.TranscribeAsync(Audio(8000), CancellationToken.None)).IsSuccess);
        Assert.Equal(50, server.Requests.Count);
        Assert.True(GC.GetTotalMemory(true) - before < 50_000_000);
    }

    private sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }
}
