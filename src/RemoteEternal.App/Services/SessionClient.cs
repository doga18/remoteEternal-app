using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteEternal.Core.Crypto;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;
using RemoteEternal.App.Media;

namespace RemoteEternal.App.Services;

/// <summary>
/// Lado cliente da sessão direta. <see cref="ConnectAsync"/> estabelece o transporte
/// (TCP + token + ACK) e só conclui após receber um <see cref="SessionHello"/> válido
/// (informações das telas do host). O handshake tem timeout explícito, então a UI nunca
/// fica indefinidamente em "Conectando...".
/// </summary>
public class SessionClient : IAsyncDisposable
{
    private static readonly TimeSpan TcpHandshakeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(12);

    private TcpClient? _tcp;
    private SecureFrameChannel? _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<SessionHello> _helloTcs = CreateHelloTcs();
    private Task<SessionHello>? _connectTask;
    private Task? _readTask;
    private int _disposeRequested;
    private int _closedRaised;

    /// <summary>
    /// Cria o TCS do hello com uma continuação que marca exceções como observadas.
    /// Quando o <c>WaitAsync</c> de <see cref="ConnectCoreAsync"/> é abandonado por timeout
    /// ou descarte, o wrapper é cancelado sem observar a tarefa interna; se o loop de leitura
    /// concluir o TCS com exceção depois disso, a continuação evita <c>UnobservedTaskException</c>.
    /// </summary>
    private static TaskCompletionSource<SessionHello> CreateHelloTcs()
    {
        var tcs = new TaskCompletionSource<SessionHello>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = tcs.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return tcs;
    }

    public MediaBuffer Media { get; } = new();
    public string? DeviceName { get; private set; }

    /// <summary>Raised when a valid <see cref="SessionHello"/> is decoded by the read loop.</summary>
    public event Action<SessionHello>? HelloReceived;

    /// <summary>Raised after the transport (TCP + token + ACK) is established, before hello.</summary>
    public event Action? Connected;

    public event Action? MediaRestarted;
    public event Action<string>? ErrorReceived;
    public event Action<string>? Ended;

    /// <summary>Raised exactly once when the underlying connection is closed.</summary>
    public event Action? Closed;

    /// <summary>
    /// Connects to the host and completes only when a valid <see cref="SessionHello"/> arrives.
    /// Throws <see cref="TimeoutException"/> or <see cref="IOException"/> with a user-friendly
    /// message (never containing IP/token) when the transport fails, the host refuses the
    /// session, or the host accepts but does not send screen information in time.
    /// </summary>
    public Task<SessionHello> ConnectAsync(string ip, int port, string tokenB64)
    {
        if (_connectTask is not null)
            throw new InvalidOperationException("A sessão de cliente já foi iniciada.");
        _connectTask = ConnectCoreAsync(ip, port, tokenB64);
        return _connectTask;
    }

    private async Task<SessionHello> ConnectCoreAsync(string ip, int port, string tokenB64)
    {
        DiagnosticLog.Write("SessionClient", "TCP conectando");
        _tcp = new TcpClient { NoDelay = true, SendBufferSize = 1024 * 1024, ReceiveBufferSize = 4 * 1024 * 1024 };

        using var tcpTimeout = new CancellationTokenSource(TcpHandshakeTimeout);
        try
        {
            await _tcp.ConnectAsync(ip, port, tcpTimeout.Token).ConfigureAwait(false);
            var stream = _tcp.GetStream();
            byte[] token = Convert.FromBase64String(tokenB64);
            await stream.WriteAsync(token, tcpTimeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(tcpTimeout.Token).ConfigureAwait(false);
            DiagnosticLog.Write("SessionClient", "Token enviado");
            byte[] ack = new byte[1];
            await FrameChannel.ReadExactlyAsync(stream, ack, 1, tcpTimeout.Token).ConfigureAwait(false);
            DiagnosticLog.Write("SessionClient", $"ACK={ack[0]} recebido");
            if (ack[0] != 1)
                throw new IOException("Acesso negado pela máquina remota.");
            _channel = SecureFrameChannel.CreateDirectional(
                stream,
                token,
                System.Text.Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1),
                "re-session"u8.ToArray(),
                SessionRole.Client);
            DiagnosticLog.Write("SessionClient", "Canal seguro criado (role=Client)");
        }
        catch (OperationCanceledException)
        {
            CloseTransport();
            DiagnosticLog.Write("SessionClient", "Fase TCP: tempo esgotado");
            ErrorLog.Write("SessionClient.ConnectCore TimeoutException: tempo esgotado na fase TCP");
            throw new TimeoutException("Tempo esgotado ao conectar ao host. Confirme que o host está na mesma rede (LAN) e que a porta de acesso está liberada no firewall.");
        }
        catch (EndOfStreamException)
        {
            CloseTransport();
            DiagnosticLog.Write("SessionClient", "Fase TCP: host encerrou a conexão durante o handshake");
            ErrorLog.Write("SessionClient.ConnectCore EndOfStreamException: host encerrou a conexão durante o handshake");
            throw new IOException("O host encerrou a conexão antes de concluir o handshake.");
        }
        catch (IOException)
        {
            CloseTransport();
            DiagnosticLog.Write("SessionClient", "Fase TCP: acesso negado pela máquina remota");
            ErrorLog.Write("SessionClient.ConnectCore IOException: acesso negado pela máquina remota");
            throw; // "Acesso negado pela máquina remota." — mensagem segura, sem IP/token
        }
        catch (Exception)
        {
            CloseTransport();
            DiagnosticLog.Write("SessionClient", "Fase TCP: exceção não categorizada");
            ErrorLog.Write("SessionClient.ConnectCore exceção não categorizada na fase TCP");
            throw new IOException("Não foi possível conectar ao host. Confirme que o host está na mesma rede (LAN) e que a porta de acesso está liberada no firewall.");
        }

        // Se o descarte começou durante a fase TCP, não dispara Connected nem inicia o loop
        // de leitura; a falha é observada por DisposeAsync ao aguardar _connectTask.
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            CloseTransport();
            throw new OperationCanceledException("Sessão encerrada durante a conexão.");
        }

        Connected?.Invoke();
        DiagnosticLog.Write("SessionClient", "Connected disparado");
        _readTask = Task.Run(ReadLoopAsync);

        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        helloTimeout.CancelAfter(HelloTimeout);
        try
        {
            DiagnosticLog.Write("SessionClient", "Aguardando hello.");
            SessionHello hello = await _helloTcs.Task.WaitAsync(helloTimeout.Token).ConfigureAwait(false);
            DiagnosticLog.Write("SessionClient", "Hello recebido/concluído.");
            return hello;
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
                throw;
            CloseTransport();
            DiagnosticLog.Write("SessionClient", "Hello não recebido no tempo limite");
            ErrorLog.Write("SessionClient.ConnectCore TimeoutException: hello não recebido no tempo limite");
            throw new TimeoutException("O host aceitou a sessão, mas não enviou as informações da tela dentro do tempo limite. Tente reconectar ou verifique o host.");
        }
    }

    private async Task ReadLoopAsync()
    {
        DiagnosticLog.Write("SessionClient", "ReadLoop iniciado");
        string? failReason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, payload) = await _channel!.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                DiagnosticLog.Write("SessionClient", $"Frame recebido type={type} bytes={payload.Length}");
                switch (type)
                {
                    case SecureFrameChannel.TypeControl:
                        failReason = HandleControl(payload);
                        break;
                    case SecureFrameChannel.TypeMedia:
                        Media.Write(payload, 0, payload.Length);
                        break;
                }
                if (failReason is not null) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelamento pedido (DisposeAsync/CloseSession) ou timeout do hello.
        }
        catch (CryptographicException)
        {
            // Falha de autenticação do frame seguro: chaves/versões diferentes ou payload adulterado.
            failReason = "O host enviou uma resposta de sessão inválida. Confirme que os dois computadores estão usando a mesma build.";
            LogReadLoopFailure(typeof(CryptographicException), failReason);
        }
        catch (EndOfStreamException)
        {
            // Host fechou sem enviar nenhum frame/hello.
            failReason = "O host encerrou a conexão antes de enviar as informações da tela.";
            LogReadLoopFailure(typeof(EndOfStreamException), failReason);
        }
        catch (IOException)
        {
            // Frame com comprimento inválido etc.: versões de protocolo diferentes.
            failReason = "O host enviou uma resposta de sessão inválida. Confirme que os dois computadores estão usando a mesma build.";
            LogReadLoopFailure(typeof(IOException), failReason);
        }
        catch (Exception)
        {
            failReason ??= "A conexão com o host foi perdida.";
            LogReadLoopFailure(null, failReason);
        }
        finally
        {
            if (!_helloTcs.Task.IsCompleted)
                _helloTcs.TrySetException(new IOException(failReason ?? "A conexão com o host foi encerrada antes do recebimento das informações da tela."));
            CloseTransport();
            RaiseClosedOnce();
        }
    }

    private static void LogReadLoopFailure(Type? exceptionType, string reason)
    {
        // Apenas etapa + tipo + mensagem sanitizada; nunca token/IP/payload/stack.
        string stage = exceptionType is null ? "desconhecido" : exceptionType.Name;
        ErrorLog.Write($"SessionClient.ReadLoop {stage}: {reason}");
        DiagnosticLog.Write("SessionClient", $"ReadLoop {stage}: {reason}");
    }

    /// <summary>
    /// Handles a control frame. Returns a non-null reason when the session/read loop should
    /// stop (invalid hello, end, or error before screen information arrives).
    /// </summary>
    private string? HandleControl(byte[] payload)
    {
        try
        {
            Envelope env = EnvelopeUtil.Parse(payload);
            DiagnosticLog.Write("SessionClient", $"Controle recebido: {env.Type}");
            switch (env.Type)
            {
                case SessionControlTypes.Hello:
                {
                    var hello = EnvelopeUtil.Data<SessionHello>(env);
                    if (hello is null || hello.Displays is null || hello.Displays.Length == 0)
                    {
                        DiagnosticLog.Write("SessionClient", "Hello inválido (sem monitores)");
                        string reason = "O host não enviou informações de tela válidas.";
                        _helloTcs.TrySetException(new IOException(reason));
                        return reason;
                    }
                    DiagnosticLog.Write("SessionClient", $"Hello decodificado: {hello.Displays.Length} monitores, defaultIndex={hello.DefaultDisplayIndex}");
                    DeviceName = hello.DeviceName;
                    HelloReceived?.Invoke(hello);
                    _helloTcs.TrySetResult(hello);
                    return null;
                }
                case SessionControlTypes.MediaRestart:
                    Media.Clear();
                    MediaRestarted?.Invoke();
                    return null;
                case SessionControlTypes.End:
                {
                    var end = EnvelopeUtil.Data<SessionEnd>(env);
                    string reason = end?.Reason ?? "Sessão encerrada";
                    if (!_helloTcs.Task.IsCompleted)
                        _helloTcs.TrySetException(new IOException(reason));
                    Ended?.Invoke(reason);
                    return reason;
                }
                case SessionControlTypes.Error:
                {
                    var err = EnvelopeUtil.Data<SessionEnd>(env);
                    string reason = err?.Reason ?? "Erro remoto";
                    ErrorReceived?.Invoke(reason);
                    if (!_helloTcs.Task.IsCompleted)
                    {
                        _helloTcs.TrySetException(new IOException(reason));
                        return reason;
                    }
                    return null;
                }
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // Envelope/data JSON inválido: versões de build/protocolo diferentes.
            return "O host enviou uma resposta de sessão inválida. Confirme que os dois computadores estão usando a mesma build.";
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

    private Task SendControlAsync(object data, CancellationToken ct = default)
    {
        if (_channel is null) return Task.CompletedTask;
        string type = data switch
        {
            SessionStart => SessionControlTypes.Start,
            SessionSwitchDisplay => SessionControlTypes.SwitchDisplay,
            SessionEnd => SessionControlTypes.End,
            _ => SessionControlTypes.Error
        };
        return _channel.SendAsync(SecureFrameChannel.TypeControl, EnvelopeUtil.Create(type, data), ct);
    }

    private void CloseTransport()
    {
        try { _tcp?.Close(); } catch { }
    }

    private void RaiseClosedOnce()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        _cts.Cancel();
        Media.Close();
        CloseTransport();
        try { await (_readTask ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        try { await (_connectTask ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
