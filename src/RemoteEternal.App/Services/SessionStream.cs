using System.IO;
using System.Threading;
using RemoteEternal.Core.Crypto;

namespace RemoteEternal.App.Services;

public sealed class SessionStream : Stream
{
    private readonly SecureFrameChannel _channel;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private long _pos;
    private volatile bool _stopped;

    // Diagnóstico observacional: contadores atômicos de blocos/bytes escritos e de
    // envios com falha. Não altera a semântica de escrita/envio (mesma ordem, mesmos
    // buffers, mesmos slots).
    private long _framesWritten;
    private long _bytesWritten;
    private long _framesFailed;
    private long _lastLoggedFrame;

    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public long FramesFailed => Interlocked.Read(ref _framesFailed);

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
        TrackWrite(count);
        _slots.Wait();
        _ = SendAndReleaseAsync(data);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_stopped) return Task.FromException(new IOException("Stream de mídia encerrado"));
        var data = new byte[count];
        Array.Copy(buffer, offset, data, 0, count);
        _pos += count;
        TrackWrite(count);
        _slots.Wait(cancellationToken);
        return SendAndReleaseAsync(data);
    }

    /// <summary>Incrementa os contadores e registra o progresso a cada 30 blocos
    /// (aproximadamente 1 vez por segundo a 30 fps), sem inundar o log.</summary>
    private void TrackWrite(int count)
    {
        long frameNumber = Interlocked.Increment(ref _framesWritten);
        Interlocked.Add(ref _bytesWritten, count);
        long last = Interlocked.Read(ref _lastLoggedFrame);
        if (frameNumber - last >= 30 &&
            Interlocked.CompareExchange(ref _lastLoggedFrame, frameNumber, last) == last)
        {
            DiagnosticLog.Write("SessionCapture",
                $"SessionStream: frames={FramesWritten} bytes={BytesWritten} falhas={FramesFailed}");
        }
    }

    private async Task SendAndReleaseAsync(byte[] data)
    {
        try
        {
            await _channel.SendAsync(SecureFrameChannel.TypeMedia, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _framesFailed);
            DiagnosticLog.Write("SessionCapture", $"SessionStream: falha ao enviar frame: {ex.GetType().Name}");
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Stop()
    {
        _stopped = true;
        DiagnosticLog.Write("SessionCapture", "SessionStream: parado");
    }

    protected override void Dispose(bool disposing)
    {
        Stop();
        base.Dispose(disposing);
    }
}
