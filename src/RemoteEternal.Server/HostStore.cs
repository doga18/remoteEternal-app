using LiteDB;

namespace RemoteEternal.Server;

/// <summary>Registro persistente de um host no diretório do servidor.</summary>
public class HostDoc
{
    /// <summary>
    /// Chave interna LiteDB (campo <c>_id</c>). O LiteDB mapeia automaticamente a
    /// propriedade <c>Id</c> para <c>_id</c>; sem ela, o objeto desserializado por
    /// <c>FindOne</c> perde o <c>_id</c> e <c>Update</c> lança
    /// "Invalid BSON data type 'Null' on field '_id'".
    /// </summary>
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string HostId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Os { get; set; } = "";
    public string AccessMode { get; set; } = "";
    public string? Salt { get; set; }
    public string? Verifier { get; set; }
    public DateTime RegisteredAt { get; set; }
}

/// <summary>
/// Persistência LiteDB do diretório de hosts (substitui AccountStore).
///
/// Cada host recebe um ID único de 6 dígitos (100000..999999) no registro.
/// O modo de acesso (assisted/unassisted) e as credenciais (salt/verifier)
/// são atualizados a cada anúncio online (<c>hostOnline</c>).
/// </summary>
public class HostStore
{
    private const int MinId = 100_000;
    private const int MaxIdExclusive = 1_000_000; // IDs válidos: 100000..999999
    private const int MaxRandomAttempts = 10;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<HostDoc> _hosts;
    private readonly object _lock = new();

    public HostStore(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
        _hosts = _db.GetCollection<HostDoc>("hosts");
        _hosts.EnsureIndex(x => x.HostId, unique: true);
    }

    public bool Exists(string hostId)
    {
        return !string.IsNullOrEmpty(hostId) && _hosts.Exists(x => x.HostId == hostId);
    }

    public HostDoc? Get(string hostId)
    {
        return string.IsNullOrEmpty(hostId) ? null : _hosts.FindOne(x => x.HostId == hostId);
    }

    /// <summary>
    /// Gera e persiste um HostId de 6 dígitos único.
    /// Sorteia entre 100000 e 999999 com no máximo <see cref="MaxRandomAttempts"/>
    /// tentativas; se todas colidirem, faz uma varredura sequencial determinística
    /// que preserva o formato de 6 dígitos e garante término enquanto houver menos
    /// de 900.000 hosts cadastrados (limite prático do espaço).
    /// </summary>
    public string CreateHost(string deviceName, string os)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < MaxRandomAttempts; attempt++)
            {
                string candidate = Random.Shared.Next(MinId, MaxIdExclusive).ToString();
                if (_hosts.Exists(x => x.HostId == candidate))
                    continue;
                InsertLocked(candidate, deviceName, os);
                return candidate;
            }

            for (int i = 0; i < MaxIdExclusive - MinId; i++)
            {
                string candidate = (MinId + i).ToString();
                if (_hosts.Exists(x => x.HostId == candidate))
                    continue;
                InsertLocked(candidate, deviceName, os);
                return candidate;
            }

            throw new InvalidOperationException("Não foi possível alocar um HostId único de 6 dígitos");
        }
    }

    /// <summary>Atualiza modo de acesso, credenciais e dados do dispositivo de um host existente.</summary>
    public bool UpdateAccess(string hostId, string accessMode, string? salt, string? verifier, string deviceName, string os)
    {
        lock (_lock)
        {
            var doc = _hosts.FindOne(x => x.HostId == hostId);
            if (doc is null)
                return false;
            doc.AccessMode = accessMode;
            doc.Salt = salt;
            doc.Verifier = verifier;
            doc.DeviceName = deviceName;
            doc.Os = os;
            return _hosts.Update(doc);
        }
    }

    private void InsertLocked(string hostId, string deviceName, string os)
    {
        _hosts.Insert(new HostDoc
        {
            HostId = hostId,
            DeviceName = deviceName,
            Os = os,
            AccessMode = "",
            RegisteredAt = DateTime.UtcNow
        });
    }
}
