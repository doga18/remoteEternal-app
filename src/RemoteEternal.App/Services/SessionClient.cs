using System.IO;
using System.Net.Sockets;
using RemoteEternal.Core.Crypto;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;
using RemoteEternal.App.Media;

namespace RemoteEternal.App.Services;

public class SessionClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private SecureFrameChannel? _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readTask;

    public MediaBuffer Media { get; } = new();
    public string? DeviceName { get; private set; }

    public event Action<SessionHello>? HelloReceived;
    public event Action? MediaRestarted;
    public event Action<string>? ErrorReceived;
    public event Action<string>? Ended;
    public event Action? Closed;

    public async Task ConnectAsync(string ip, int port, string tokenB64)
    {
        _tcp = new TcpClient { NoDelay = true, SendBufferSize = 1024 * 1024, ReceiveBufferSize = 4 * 1024 * 1024 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await _tcp.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
        var stream = _tcp.GetStream();
        byte[] token = Convert.FromBase64String(tokenB64);
        await stream.WriteAsync(token, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        byte[] ack = new byte[1];
        await FrameChannel.ReadExactlyAsync(stream, ack, 1, timeout.Token).ConfigureAwait(false);
        if (ack[0] != 1) throw new IOException("Acesso negado pela máquina remota");
        _channel = SecureFrameChannel.CreateDirectional(stream, token, System.Text.Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1), "re-session"u8.ToArray(), SessionRole.Client);
        _readTask = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, payload) = await _channel!.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                switch (type)
                {
                    case SecureFrameChannel.TypeControl:
                        HandleControl(payload);
                        break;
                    case SecureFrameChannel.TypeMedia:
                        Media.Write(payload, 0, payload.Length);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            Closed?.Invoke();
        }
    }

    private void HandleControl(byte[] payload)
    {
        var env = EnvelopeUtil.Parse(payload);
        switch (env.Type)
        {
            case SessionControlTypes.Hello:
                var hello = EnvelopeUtil.Data<SessionHello>(env);
                if (hello is not null)
                {
                    DeviceName = hello.DeviceName;
                    HelloReceived?.Invoke(hello);
                }
                break;
            case SessionControlTypes.MediaRestart:
                Media.Clear();
                MediaRestarted?.Invoke();
                break;
            case SessionControlTypes.End:
                var end = EnvelopeUtil.Data<SessionEnd>(env);
                Ended?.Invoke(end?.Reason ?? "Sessão encerrada");
                break;
            case SessionControlTypes.Error:
                var err = EnvelopeUtil.Data<SessionEnd>(env);
                ErrorReceived?.Invoke(err?.Reason ?? "Erro remoto");
                break;
        }
    }

    public Task SendInputAsync(byte[] payload)
    {
        return _channel is null ? Task.CompletedTask : _channel.SendAsync(SecureFrameChannel.TypeInput, payload);
    }

    public Task SendStartAsync(string displayId, int fps, int bitrateKbps, int quality, bool audio)
    {
        return SendControlAsync(new SessionStart(displayId, fps, bitrateKbps, quality, audio));
    }

    public Task SendSwitchDisplayAsync(string displayId)
    {
        return SendControlAsync(new SessionSwitchDisplay(displayId));
    }

    public Task SendEndAsync()
    {
        return SendControlAsync(new SessionEnd("Encerrada pelo usuário"));
    }

    private Task SendControlAsync(object data)
    {
        if (_channel is null) return Task.CompletedTask;
        string type = data switch
        {
            SessionStart => SessionControlTypes.Start,
            SessionSwitchDisplay => SessionControlTypes.SwitchDisplay,
            SessionEnd => SessionControlTypes.End,
            _ => SessionControlTypes.Error
        };
        return _channel.SendAsync(SecureFrameChannel.TypeControl, EnvelopeUtil.Create(type, data));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Media.Close();
        try { await (_readTask ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        _tcp?.Close();
        _cts.Dispose();
    }
}
