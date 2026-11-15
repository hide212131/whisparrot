using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Whisparrot.Tests.Fixtures;

/// <summary>ループバックの平文 HTTP モック（OpenAI 互換 /chat/completions の試験用）。リクエストの本文まで記録する。</summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<TestRequest, TestResponse> _handler;
    private readonly object _lock = new();
    private readonly List<TestRequest> _requests = [];

    public TestHttpServer(Func<TestRequest, TestResponse> handler)
    {
        _handler = handler;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public IReadOnlyList<TestRequest> Requests { get { lock (_lock) return _requests.ToList(); } }

    /// <summary>正常な chat.completions 応答。</summary>
    public static TestResponse Completion(string content, string? finishReason = "stop") =>
        Json(200, new { id = "x", choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finishReason } } });

    public static TestResponse Json(int status, object body) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(body), new() { ["Content-Type"] = "application/json" });

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                var stream = tcp.GetStream();
                var head = await HttpText.ReadHead(stream, _cts.Token);
                if (head is null) return;
                var req = HttpText.Parse(head);
                req = req with { Body = await HttpText.ReadBody(stream, req, _cts.Token) };
                lock (_lock) _requests.Add(req);
                var r = _handler(req);
                var sb = new StringBuilder($"HTTP/1.1 {r.Status} X\r\nContent-Length: {r.ContentLength ?? r.Body.Length}\r\nConnection: close\r\n");
                foreach (var (k, v) in r.Headers ?? []) sb.Append($"{k}: {v}\r\n");
                sb.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                if (r.HoldAfter is { } n && n < r.Body.Length)
                {
                    await stream.WriteAsync(r.Body.AsMemory(0, n));
                    await stream.FlushAsync();
                    if (r.Release is not null) await Task.WhenAny(r.Release, Task.Delay(30000, _cts.Token));
                    await stream.WriteAsync(r.Body.AsMemory(n));
                }
                else await stream.WriteAsync(r.Body);
                await stream.FlushAsync();
            }
            catch { /* クライアントが取消・切断した */ }
        }
    }

    public void Dispose() { _cts.Cancel(); _listener.Stop(); }
}
