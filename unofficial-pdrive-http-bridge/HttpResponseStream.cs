using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WatsonWebserver.Core;

namespace unofficial_pdrive_http_bridge;

public sealed class HttpResponseStream(HttpContextBase ctx) : Stream
{
    private readonly HttpContextBase _ctx = ctx;
    private long _position = 0;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _position;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("set Position not supported");
    }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException("Write not implemented (Use the async version)");
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _ctx.Response.SendChunk(buffer.ToArray(), false, cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _ctx.Response.SendChunk(buffer.AsSpan(offset, count).ToArray(), false, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Read not supported");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("Seek not supported");
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("SetLength not supported");
    }
}
