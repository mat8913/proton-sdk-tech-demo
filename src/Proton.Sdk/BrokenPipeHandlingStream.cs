using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Proton.Sdk;

internal sealed class BrokenPipeHandlingStream(Stream innerStream, int socketStrategyIndex, ILogger logger) : Stream
{
    public const int PosixEpipeErrorCode = 32;

    private static readonly Func<SocketsHttpConnectionContext, ILogger, CancellationToken, ValueTask<Stream>>[] SocketStrategies =
    [
        ApplyDefaultSocketStrategy,
        ApplyDualModeSocketStrategy,
        ApplyForcedIPv4SocketStrategy,
    ];

    public static readonly int SuggestedNumberOfRetries = SocketStrategies.Length - 1;

    private static int _currentSocketStrategyIndex;

    private readonly Stream _innerStream = innerStream;
    private readonly int _socketStrategyIndex = socketStrategyIndex;
    private readonly ILogger _logger = logger;

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public static async ValueTask<Stream> CreateAsync(
        SocketsHttpConnectionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var currentSocketStrategyIndex = _currentSocketStrategyIndex;

        var innerStream = await SocketStrategies[currentSocketStrategyIndex].Invoke(context, logger, cancellationToken).ConfigureAwait(false);

        return new BrokenPipeHandlingStream(innerStream, currentSocketStrategyIndex, logger);
    }

    public override void Flush()
    {
        _innerStream.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _innerStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _innerStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _innerStream.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return HandleBrokenPipeAsync(ct => _innerStream.WriteAsync(buffer, offset, count, ct), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return HandleBrokenPipeAsync(async ct => await _innerStream.WriteAsync(buffer, ct).ConfigureAwait(false), cancellationToken);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _innerStream.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        _innerStream.WriteByte(value);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _innerStream.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return HandleBrokenPipeAsync(ct => _innerStream.ReadAsync(buffer, offset, count, ct), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return await HandleBrokenPipeAsync(async ct => await _innerStream.ReadAsync(buffer, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(Span<byte> buffer)
    {
        return _innerStream.Read(buffer);
    }

    public override int ReadByte()
    {
        return _innerStream.ReadByte();
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return _innerStream.BeginRead(buffer, offset, count, callback, state);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return _innerStream.BeginWrite(buffer, offset, count, callback, state);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        return _innerStream.EndRead(asyncResult);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        _innerStream.EndWrite(asyncResult);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return HandleBrokenPipeAsync(ct => _innerStream.FlushAsync(ct), cancellationToken).AsTask();
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        return HandleBrokenPipeAsync(ct => _innerStream.CopyToAsync(destination, bufferSize, ct), cancellationToken).AsTask();
    }

    public override void CopyTo(Stream destination, int bufferSize)
    {
        _innerStream.CopyTo(destination, bufferSize);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _innerStream.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _innerStream.Dispose();
        }
    }

    private static async ValueTask<Stream> ApplyDefaultSocketStrategy(SocketsHttpConnectionContext context, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Using default socket strategy");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

        var stream = new NetworkStream(socket, true);

        return stream;
    }

    private static async ValueTask<Stream> ApplyDualModeSocketStrategy(
        SocketsHttpConnectionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Using dual mode socket strategy");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        // The socket should already be dual mode, but let's ensure it
        socket.DualMode = true;

        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

        var stream = new NetworkStream(socket, true);

        return stream;
    }

    private static async ValueTask<Stream> ApplyForcedIPv4SocketStrategy(
        SocketsHttpConnectionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Using forced IPv4 socket strategy");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

        return new NetworkStream(socket, true);
    }

    private async Task<T> HandleBrokenPipeAsync<T>(Func<CancellationToken, Task<T>> func, CancellationToken cancellationToken)
    {
        try
        {
            return await func.Invoke(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e.GetBaseException() is SocketException socketException)
        {
            _logger.LogError(
                e,
                "Encountered socket exception: ErrorCode = {ErrorCode}, SocketErrorCode = {SocketErrorCode}",
                socketException.ErrorCode,
                socketException.SocketErrorCode);

            if (socketException is { ErrorCode: PosixEpipeErrorCode } or { SocketErrorCode: SocketError.Shutdown })
            {
                TrySwitchStrategy();
            }

            throw;
        }
    }

    private async ValueTask HandleBrokenPipeAsync(Func<CancellationToken, Task> func, CancellationToken cancellationToken)
    {
        try
        {
            await func.Invoke(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e.GetBaseException() is SocketException socketException)
        {
            _logger.LogError(
                e,
                "Encountered socket exception: ErrorCode = {ErrorCode}, SocketErrorCode = {SocketErrorCode}",
                socketException.ErrorCode,
                socketException.SocketErrorCode);

            if (socketException is { ErrorCode: PosixEpipeErrorCode } or { SocketErrorCode: SocketError.Shutdown })
            {
                TrySwitchStrategy();
            }

            throw;
        }
    }

    private void TrySwitchStrategy()
    {
        if (_socketStrategyIndex >= SocketStrategies.Length - 1)
        {
            return;
        }

        Interlocked.CompareExchange(ref _currentSocketStrategyIndex, _socketStrategyIndex + 1, _socketStrategyIndex);
    }
}
