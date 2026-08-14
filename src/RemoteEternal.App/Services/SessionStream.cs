using System.IO;
using RemoteEternal.Core.Crypto;

namespace RemoteEternal.App.Services;

public sealed class SessionStream : Stream
{
    private readonly SecureFrameChannel _channel;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private long _pos;
    private volatile bool _stopped;

    public SessionStream(SecureFrameChannel channel) => _channel = channel;

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => long.MaxValue;
    public override long Position { get => _pos; set => _pos = value; }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override long Seek(long offset, SeekOrigin origin) => _pos;

    public override void SetLength(long value) { }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_stopped) throw new IOException("Stream de mídia encerrado");
        var data = new byte[count];
        Array.Copy(buffer, offset, data, 0, count);
        _pos += count;
        _slots.Wait();
        _ = SendAndReleaseAsync(data);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_stopped) return Task.FromException(new IOException("Stream de mídia encerrado"));
        var data = new byte[count];
        Array.Copy(buffer, offset, data, 0, count);
        _pos += count;
        _slots.Wait(cancellationToken);
        return SendAndReleaseAsync(data);
    }

    private async Task SendAndReleaseAsync(byte[] data)
    {
        try
        {
            await _channel.SendAsync(SecureFrameChannel.TypeMedia, data).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Stop() => _stopped = true;

    protected override void Dispose(bool disposing)
    {
        Stop();
        base.Dispose(disposing);
    }
}
