namespace RemoteEternal.Server;

/// <summary>
/// Limite de taxa anti brute force para lookups de host.
///
/// HostId tem apenas 6 dígitos e é fácil de enumerar, então falhas de lookup
/// (senha incorreta ou host inexistente) são contadas por endereço IP remoto.
/// Após <see cref="MaxFailures"/> falhas dentro de <see cref="Window"/>, o IP fica
/// bloqueado por <see cref="BlockDuration"/>. Implementação em memória com lock;
/// nenhum dado sensível é retido.
/// </summary>
public class RateLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    public int MaxFailures { get; }
    public TimeSpan Window { get; }
    public TimeSpan BlockDuration { get; }

    public RateLimiter(int maxFailures = 5, TimeSpan? window = null, TimeSpan? blockDuration = null)
    {
        MaxFailures = maxFailures;
        Window = window ?? TimeSpan.FromSeconds(60);
        BlockDuration = blockDuration ?? TimeSpan.FromSeconds(60);
    }

    private sealed class Entry
    {
        public int Count;
        public DateTime WindowStartUtc;
        public DateTime? BlockedUntilUtc;
    }

    /// <summary>True quando o endereço está dentro da janela de bloqueio.</summary>
    public bool IsBlocked(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return false;
            var now = DateTime.UtcNow;
            if (entry.BlockedUntilUtc is { } until && now < until)
                return true;
            if (entry.BlockedUntilUtc is not null || now - entry.WindowStartUtc >= Window)
                _entries.Remove(key);
            return false;
        }
    }

    /// <summary>Registra uma falha de lookup para o endereço. Ignora quando já bloqueado.</summary>
    public void RecordFailure(string key)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Count = 0, WindowStartUtc = now };
                _entries[key] = entry;
            }

            if (entry.BlockedUntilUtc is { } until && now < until)
                return;

            if (entry.BlockedUntilUtc is not null || now - entry.WindowStartUtc >= Window)
            {
                // Bloqueio expirado ou janela reiniciada: zera o estado.
                entry.BlockedUntilUtc = null;
                entry.WindowStartUtc = now;
                entry.Count = 0;
            }

            entry.Count++;
            if (entry.Count >= MaxFailures)
            {
                entry.BlockedUntilUtc = now + BlockDuration;
                entry.WindowStartUtc = now;
                entry.Count = 0;
            }
        }
    }
}
