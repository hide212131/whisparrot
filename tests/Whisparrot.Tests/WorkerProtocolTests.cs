using System.Buffers.Binary;
using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public async Task Frame_roundtrip()
    {
        var ms = new MemoryStream();
        await WorkerProtocol.WriteAsync(ms, FrameType.Hello, new byte[] { 1, 2, 3 }, default);
        ms.Position = 0;
        var (type, payload) = await WorkerProtocol.ReadAsync(ms, default);
        Assert.Equal(FrameType.Hello, type);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
    }

    [Fact]
    public async Task Request_roundtrip_keeps_every_sample()
    {
        float[] samples = [0f, 0.5f, -0.25f, 1f];
        var (header, parsed) = WorkerProtocol.ParseRequest(WorkerProtocol.BuildRequest(new RequestHeader("ja", 4, samples.Length), samples));
        Assert.Equal("ja", header.Language);
        Assert.Equal(samples, parsed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Oversized_or_negative_length_is_rejected()
    {
        foreach (var len in new[] { WorkerProtocol.MaxPayload + 1, -1, int.MaxValue })
        {
            var buf = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(buf, len);
            buf[4] = (byte)FrameType.Request;
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkerProtocol.ReadAsync(new MemoryStream(buf), default));
        }
    }

    [Fact]
    public async Task Unknown_frame_type_is_rejected()
    {
        var buf = new byte[] { 0, 0, 0, 0, 99 };
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerProtocol.ReadAsync(new MemoryStream(buf), default));
    }

    [Fact]
    public async Task Truncated_stream_is_reported_as_end_of_stream()
    {
        var buf = new byte[] { 10, 0, 0, 0, (byte)FrameType.Request, 1, 2 };
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkerProtocol.ReadAsync(new MemoryStream(buf), default));
    }

    [Fact]
    public void Request_with_mismatched_pcm_length_is_rejected()
    {
        var good = WorkerProtocol.BuildRequest(new RequestHeader("ja", 1, 4), new float[4]);
        Assert.Throws<InvalidDataException>(() => WorkerProtocol.ParseRequest(good[..^4]));
        Assert.Throws<InvalidDataException>(() => WorkerProtocol.ParseRequest([1, 2]));
    }
}
