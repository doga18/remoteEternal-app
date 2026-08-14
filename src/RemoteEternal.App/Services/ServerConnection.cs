using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.App.Services;

public class ServerConnection : IAsyncDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<Envelope>>> _pending = new();
    private readonly ConcurrentDictionary<string, Action<Envelope>> _handlers = new();

    public bool IsConnected => _tcp?.Connected == true;

    public event Action? Disconnected;
    public event Action<Envelope>? Notification;

    public void On(string type, Action<Envelope> handler) => _handlers[type] = handler;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_stream is not null)
            {
                byte[] frame = await FrameChannel.ReadFrameAsync(_stream, _cts!.Token).ConfigureAwait(false);
                var env = EnvelopeUtil.Parse(frame);
                if (_pending.TryGetValue(env.Type, out var q) && q.TryDequeue(out var tcs))
                {
                    tcs.TrySetResult(env);
                }
                else
                {
                    Notification?.Invoke(env);
                    if (_handlers.TryGetValue(env.Type, out var handler)) handler(env);
                }
            }
        }
        catch (Exception)
        {
            // connection lost
        }
        finally
        {
            foreach (var q in _pending.Values)
            {
                while (q.TryDequeue(out var tcs))
                    tcs.TrySetException(new IOException("Conexão com o servidor perdida"));
            }
            Disconnected?.Invoke();
        }
    }

    public async Task<Envelope> SendAsync(string type, object? data = null, TimeSpan? timeout = null)
    {
        if (_stream is null) throw new IOException("Não conectado");
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.GetOrAdd(type, _ => new ConcurrentQueue<TaskCompletionSource<Envelope>>()).Enqueue(tcs);
        try
        {
            await FrameChannel.WriteFrameAsync(_stream, EnvelopeUtil.Create(type, data), _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch
        {
            if (_pending.TryGetValue(type, out var q))
                q.TryDequeue(out _);
            throw;
        }
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        await using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException("Tempo esgotado")));
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task SendNoReplyAsync(string type, object? data = null)
    {
        if (_stream is null) return Task.CompletedTask;
        return FrameChannel.WriteFrameAsync(_stream, EnvelopeUtil.Create(type, data), _cts?.Token ?? default);
    }

    /// <summary>Registra este computador no servidor e obtém um HostId de 6 dígitos.</summary>
    public async Task<RegisterHostResult> RegisterHostAsync(string deviceName, string os)
    {
        var env = await SendAsync(MsgTypes.RegisterHost, new RegisterHostRequest(deviceName, os)).ConfigureAwait(false);
        return EnvelopeUtil.Data<RegisterHostResult>(env) ?? new RegisterHostResult(false, null, "Resposta inválida");
    }

    /// <summary>Publica o host como online com porta de escuta e modo de acesso.
    /// Salt/Verifier (base64) são obrigatórios em unassisted e devem ser null em assisted.</summary>
    public async Task<HostOnlineResult> HostOnlineAsync(
        string hostId, string deviceName, string os, int listenPort, string accessMode, string? saltB64, string? verifierB64)
    {
        var env = await SendAsync(MsgTypes.HostOnline,
            new HostOnlineRequest(hostId, deviceName, os, listenPort, accessMode, saltB64, verifierB64)).ConfigureAwait(false);
        return EnvelopeUtil.Data<HostOnlineResult>(env) ?? new HostOnlineResult(false, "Resposta inválida");
    }

    /// <summary>Obtém o modo de acesso e o salt do host (salt só existe em unassisted).</summary>
    public async Task<GetHostSaltResult> GetHostSaltAsync(string hostId)
    {
        var env = await SendAsync(MsgTypes.GetHostSalt, new GetHostSaltRequest(hostId)).ConfigureAwait(false);
        return EnvelopeUtil.Data<GetHostSaltResult>(env) ?? new GetHostSaltResult(false, null, null, "Resposta inválida");
    }

    /// <summary>Solicita conexão com o host. Em unassisted, AuthHash = PBKDF2 da senha com o salt obtido.
    /// O servidor aguarda a aprovação/recusa do host (20s); o timeout local é de 30s.</summary>
    public async Task<LookupResult> LookupAsync(string hostId, string? authHash)
    {
        var env = await SendAsync(MsgTypes.Lookup, new LookupRequest(hostId, authHash), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return EnvelopeUtil.Data<LookupResult>(env) ?? new LookupResult(false, null, 0, null, "Resposta inválida");
    }

    /// <summary>Resposta do host ao servidor sobre uma conexão solicitada (connectNotify).</summary>
    public Task SendConnectAckAsync(string hostId, bool accepted, int listenPort)
    {
        return SendNoReplyAsync(MsgTypes.ConnectAck, new ConnectAck(hostId, accepted, listenPort));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _tcp?.Close();
        await Task.CompletedTask;
    }
}
