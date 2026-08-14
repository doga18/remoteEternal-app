using System.Net.Sockets;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.Server;

/// <summary>Host disponível para conexão, vinculado à sessão de controle que o anunciou.</summary>
public class OnlineHost
{
    public required string HostId { get; init; }
    public required string DeviceName { get; init; }
    public required string Os { get; init; }
    public required string Ip { get; init; }
    public required int ListenPort { get; set; }
    public required string AccessMode { get; init; }
    public required ClientSession Session { get; init; }
}

/// <summary>Lookup de conexão aguardando o ConnectAck do host (timeout/cancelamento externo).</summary>
public class PendingLookup
{
    public required string HostId { get; init; }
    public required TaskCompletionSource<ConnectAck> Ack { get; init; }
}

/// <summary>
/// Diretório em memória de hosts online e de conexões pendentes de aprovação.
///
/// - Chave de online: HostId (um host por conexão de controle).
/// - Chave de pendência: HostId, porque o contrato <c>ConnectAck</c> identifica o
///   host, não o token de sessão; portanto há no máximo uma conexão pendente por host.
/// - Desconexão do host remove suas entradas e cancela pendências associadas.
/// </summary>
public class ClientRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, OnlineHost> _online = new(); // key: hostId
    private readonly Dictionary<string, PendingLookup> _pending = new(); // key: hostId

    public void SetOnline(OnlineHost host)
    {
        lock (_lock) _online[host.HostId] = host;
    }

    public void RemoveOnline(ClientSession session)
    {
        lock (_lock)
        {
            var hostIds = _online.Where(kv => kv.Value.Session == session).Select(kv => kv.Key).ToList();
            foreach (var hostId in hostIds)
            {
                _online.Remove(hostId);
                RemovePendingLocked(hostId, new IOException("O host desconectou durante a conexão"));
            }
        }
    }

    public OnlineHost? GetOnline(string hostId)
    {
        lock (_lock)
        {
            return string.IsNullOrEmpty(hostId)
                ? null
                : _online.TryGetValue(hostId, out var host) ? host : null;
        }
    }

    /// <summary>Cria uma pendência para o host; retorna null se já houver uma ativa.</summary>
    public PendingLookup? AddPending(string hostId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConnectAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingLookup { HostId = hostId, Ack = tcs };
        lock (_lock)
        {
            if (_pending.ContainsKey(hostId))
                return null;
            _pending[hostId] = pending;
        }
        ct.Register(() =>
        {
            lock (_lock) RemovePendingLocked(hostId, null);
        });
        return pending;
    }

    public void CompletePending(string hostId, ConnectAck ack)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(hostId, out var pending))
            {
                _pending.Remove(hostId);
                pending.Ack.TrySetResult(ack);
            }
        }
    }

    private void RemovePendingLocked(string hostId, Exception? failure)
    {
        if (!_pending.Remove(hostId, out var pending))
            return;
        if (failure is not null)
            pending.Ack.TrySetException(failure);
        else
            pending.Ack.TrySetCanceled();
    }
}
