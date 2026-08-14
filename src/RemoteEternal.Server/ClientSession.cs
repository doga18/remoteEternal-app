using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.Server;

public class ClientSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    private const int MaxDeviceNameLength = 128;
    private const int MaxOsLength = 64;
    private const int PendingLookupTimeoutSeconds = 20;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly RemoteEternalServer _server;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _hostId;

    /// <summary>HostId ao qual esta conexão pertence (registerHost ou hostOnline).</summary>
    public string? HostId => _hostId;

    public ClientSession(TcpClient tcp, RemoteEternalServer server)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _server = server;
    }

    public async Task SendAsync(string type, object? data = null, CancellationToken ct = default)
    {
        byte[] payload = EnvelopeUtil.Create(type, data);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FrameChannel.WriteFrameAsync(_stream, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task RunAsync()
    {
        var remote = _tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        Log($"Cliente {Id} conectado de {remote}");
        try
        {
            while (true)
            {
                byte[] frame = await FrameChannel.ReadFrameAsync(_stream).ConfigureAwait(false);
                var env = EnvelopeUtil.Parse(frame);
                await DispatchAsync(env).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException)
        {
            Log($"Cliente {Id} desconectado");
        }
        catch (IOException ex)
        {
            Log($"Cliente {Id} erro de conexão: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"Cliente {Id} erro: {ex.Message}");
        }
        finally
        {
            // Remove hosts online desta conexão e cancela pendências do host;
            // tokens de sessão independentes do pareamento não são revogados aqui.
            _server.Registry.RemoveOnline(this);
            _tcp.Close();
        }
    }

    private async Task DispatchAsync(Envelope env)
    {
        switch (env.Type)
        {
            case MsgTypes.RegisterHost:
                await HandleRegisterHostAsync(env).ConfigureAwait(false);
                break;
            case MsgTypes.HostOnline:
                await HandleHostOnlineAsync(env).ConfigureAwait(false);
                break;
            case MsgTypes.GetHostSalt:
                await HandleGetHostSaltAsync(env).ConfigureAwait(false);
                break;
            case MsgTypes.Lookup:
                await HandleLookupAsync(env).ConfigureAwait(false);
                break;
            case MsgTypes.ConnectAck:
                HandleConnectAck(env);
                break;
            case MsgTypes.Ping:
                break;
            default:
                Log($"Comando desconhecido: {env.Type}");
                break;
        }
    }

    private async Task HandleRegisterHostAsync(Envelope env)
    {
        var req = EnvelopeUtil.Data<RegisterHostRequest>(env);
        if (req is null || string.IsNullOrWhiteSpace(req.DeviceName) || string.IsNullOrWhiteSpace(req.Os))
        {
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(false, null, "Requisição inválida")).ConfigureAwait(false);
            return;
        }
        if (req.DeviceName.Length > MaxDeviceNameLength || req.Os.Length > MaxOsLength)
        {
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(false, null, "Dados inválidos")).ConfigureAwait(false);
            return;
        }
        if (_hostId is not null)
        {
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(false, null, "Esta conexão já registrou um host")).ConfigureAwait(false);
            return;
        }
        if (!_server.AllowRegistration)
        {
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(false, null, "Registro desabilitado no servidor")).ConfigureAwait(false);
            return;
        }
        try
        {
            string hostId = _server.Hosts.CreateHost(req.DeviceName.Trim(), req.Os.Trim());
            _hostId = hostId;
            Log($"Host registrado: {hostId} ({req.DeviceName})");
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(true, hostId, null)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Falha ao registrar host: {ex.Message}");
            await SendAsync(MsgTypes.RegisterHostResult, new RegisterHostResult(false, null, "Erro interno ao registrar")).ConfigureAwait(false);
        }
    }

    private async Task HandleHostOnlineAsync(Envelope env)
    {
        var req = EnvelopeUtil.Data<HostOnlineRequest>(env);
        if (req is null || string.IsNullOrWhiteSpace(req.HostId))
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Requisição inválida")).ConfigureAwait(false);
            return;
        }
        if (req.ListenPort is < 1 or > 65535)
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Porta de escuta inválida")).ConfigureAwait(false);
            return;
        }
        if (req.AccessMode is not (HostAccess.Assisted or HostAccess.Unassisted))
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Modo de acesso inválido")).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.DeviceName) || req.DeviceName.Length > MaxDeviceNameLength ||
            string.IsNullOrWhiteSpace(req.Os) || req.Os.Length > MaxOsLength)
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Dados do dispositivo inválidos")).ConfigureAwait(false);
            return;
        }
        if (req.AccessMode == HostAccess.Unassisted)
        {
            if (string.IsNullOrEmpty(req.Salt) || string.IsNullOrEmpty(req.Verifier) ||
                !IsValidSaltVerifier(req.Salt, req.Verifier))
            {
                await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Salt e verifier são obrigatórios no modo sem assistência")).ConfigureAwait(false);
                return;
            }
        }
        else if (req.Salt is not null || req.Verifier is not null)
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Modo assistido não aceita salt/verifier")).ConfigureAwait(false);
            return;
        }
        if (_hostId is not null && !string.Equals(_hostId, req.HostId, StringComparison.Ordinal))
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Esta conexão já pertence a outro host")).ConfigureAwait(false);
            return;
        }
        var existing = _server.Registry.GetOnline(req.HostId);
        if (existing is not null && existing.Session != this)
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Host já está online em outra conexão")).ConfigureAwait(false);
            return;
        }
        if (!_server.Hosts.Exists(req.HostId))
        {
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "ID não encontrado")).ConfigureAwait(false);
            return;
        }

        string ip = ((IPEndPoint?)_tcp.Client.RemoteEndPoint)?.Address.ToString() ?? "127.0.0.1";
        try
        {
            // Persistência primeiro: só anuncia o host online quando o diretório for atualizado.
            if (!_server.Hosts.UpdateAccess(req.HostId, req.AccessMode, req.Salt, req.Verifier, req.DeviceName, req.Os))
            {
                await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "ID não encontrado")).ConfigureAwait(false);
                return;
            }
            _server.Registry.SetOnline(new OnlineHost
            {
                HostId = req.HostId,
                DeviceName = req.DeviceName,
                Os = req.Os,
                Ip = ip,
                ListenPort = req.ListenPort,
                AccessMode = req.AccessMode,
                Session = this
            });
        }
        catch (Exception ex)
        {
            // Falha de persistência não deve derrubar a conexão; responde erro ao host.
            Log($"Falha ao registrar host online: {ex.Message}");
            await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(false, "Falha ao atualizar o host")).ConfigureAwait(false);
            return;
        }
        _hostId = req.HostId;
        Log($"Host online: {req.HostId} ({req.DeviceName}) ip={ip}:{req.ListenPort} modo={req.AccessMode}");
        await SendAsync(MsgTypes.HostOnlineResult, new HostOnlineResult(true, null)).ConfigureAwait(false);
    }

    private async Task HandleGetHostSaltAsync(Envelope env)
    {
        var req = EnvelopeUtil.Data<GetHostSaltRequest>(env);
        if (req is null || string.IsNullOrWhiteSpace(req.HostId))
        {
            await SendAsync(MsgTypes.GetHostSaltResult, new GetHostSaltResult(false, null, null, "Requisição inválida")).ConfigureAwait(false);
            return;
        }
        var host = _server.Hosts.Get(req.HostId);
        if (host is null)
        {
            await SendAsync(MsgTypes.GetHostSaltResult, new GetHostSaltResult(false, null, null, "ID não encontrado")).ConfigureAwait(false);
            return;
        }
        if (host.AccessMode is not (HostAccess.Assisted or HostAccess.Unassisted))
        {
            await SendAsync(MsgTypes.GetHostSaltResult, new GetHostSaltResult(false, null, null, "Host ainda não disponível")).ConfigureAwait(false);
            return;
        }
        bool unassisted = host.AccessMode == HostAccess.Unassisted;
        await SendAsync(MsgTypes.GetHostSaltResult, new GetHostSaltResult(true, host.AccessMode, unassisted ? host.Salt : null, null)).ConfigureAwait(false);
    }

    private async Task HandleLookupAsync(Envelope env)
    {
        var req = EnvelopeUtil.Data<LookupRequest>(env);
        if (req is null || string.IsNullOrWhiteSpace(req.HostId))
        {
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Requisição inválida")).ConfigureAwait(false);
            return;
        }

        string ip = ((IPEndPoint?)_tcp.Client.RemoteEndPoint)?.Address.ToString() ?? "127.0.0.1";
        if (_server.RateLimiter.IsBlocked(ip))
        {
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Muitas tentativas. Tente novamente em instantes")).ConfigureAwait(false);
            return;
        }

        var host = _server.Registry.GetOnline(req.HostId);
        if (host is null || _server.Hosts.Get(req.HostId) is not { } hostDoc)
        {
            _server.RateLimiter.RecordFailure(ip);
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Host offline ou ID não encontrado")).ConfigureAwait(false);
            return;
        }

        bool unassisted = host.AccessMode == HostAccess.Unassisted;
        if (unassisted && !VerifyPassword(hostDoc, req.AuthHash))
        {
            _server.RateLimiter.RecordFailure(ip);
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Senha incorreta")).ConfigureAwait(false);
            return;
        }

        string sessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(PendingLookupTimeoutSeconds));
        var pending = _server.Registry.AddPending(req.HostId, cts.Token);
        if (pending is null)
        {
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "O host está processando outra solicitação")).ConfigureAwait(false);
            return;
        }

        try
        {
            await host.Session.SendAsync(
                MsgTypes.ConnectNotify,
                new ConnectNotify(sessionToken, "Cliente remoto", "", host.AccessMode == HostAccess.Assisted))
                .ConfigureAwait(false);

            var ack = await pending.Ack.Task.ConfigureAwait(false);
            if (ack is null || !ack.Accepted)
            {
                await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Conexão recusada pelo host")).ConfigureAwait(false);
                return;
            }
            if (ack.ListenPort is < 1 or > 65535)
            {
                await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Porta de conexão inválida")).ConfigureAwait(false);
                return;
            }
            Log($"Lookup OK: cliente {ip} -> host {req.HostId}");
            await SendAsync(MsgTypes.LookupResult, new LookupResult(true, host.Ip, ack.ListenPort, sessionToken, null)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Tempo esgotado")).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Host desconectado")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Erro no lookup: {ex.Message}");
            await SendAsync(MsgTypes.LookupResult, new LookupResult(false, null, 0, null, "Falha na conexão")).ConfigureAwait(false);
        }
    }

    private void HandleConnectAck(Envelope env)
    {
        var ack = EnvelopeUtil.Data<ConnectAck>(env);
        if (ack is null || string.IsNullOrEmpty(ack.HostId))
            return;
        // Somente a conexão de controle do próprio host pode confirmar a solicitação.
        if (!string.Equals(ack.HostId, _hostId, StringComparison.Ordinal))
            return;
        if (_server.Registry.GetOnline(ack.HostId)?.Session != this)
            return;
        _server.Registry.CompletePending(ack.HostId, ack);
    }

    private static bool IsValidSaltVerifier(string saltBase64, string verifierBase64)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] verifier = Convert.FromBase64String(verifierBase64);
            // PasswordHasher do Core: salt = 16 bytes, verifier = PBKDF2/SHA256 = 32 bytes.
            return salt.Length == 16 && verifier.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool VerifyPassword(HostDoc host, string? authHash)
    {
        if (string.IsNullOrEmpty(host.Verifier) || string.IsNullOrEmpty(authHash))
            return false;
        byte[] expected;
        byte[] provided;
        try
        {
            expected = Convert.FromBase64String(host.Verifier);
            provided = Convert.FromBase64String(authHash);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length != provided.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
