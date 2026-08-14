using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteEternal.Core.Protocol;

public static class MsgTypes
{
    public const string RegisterHost = "registerHost";
    public const string RegisterHostResult = "registerHostResult";
    public const string HostOnline = "hostOnline";
    public const string HostOnlineResult = "hostOnlineResult";
    public const string GetHostSalt = "getHostSalt";
    public const string GetHostSaltResult = "getHostSaltResult";
    public const string Lookup = "lookup";
    public const string LookupResult = "lookupResult";
    public const string ConnectNotify = "connectNotify";
    public const string ConnectAck = "connectAck";
    public const string Ping = "ping";
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public sealed record Envelope(string Type, string? DataJson);

public static class EnvelopeUtil
{
    public static byte[] Create(string type, object? data = null)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            if (data is not null)
            {
                w.WritePropertyName("data");
                JsonSerializer.Serialize(w, data, data.GetType(), JsonUtil.Options);
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static Envelope Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string type = root.GetProperty("type").GetString() ?? string.Empty;
        string? dataJson = root.TryGetProperty("data", out var d) ? d.GetRawText() : null;
        return new Envelope(type, dataJson);
    }

    public static T? Data<T>(Envelope env) =>
        string.IsNullOrEmpty(env.DataJson) ? default : JsonSerializer.Deserialize<T>(env.DataJson, JsonUtil.Options);
}

/// <summary>Modo de acesso do host: acesso assistido (aprovação manual) ou não assistido (senha).</summary>
public static class HostAccess
{
    public const string Assisted = "assisted";
    public const string Unassisted = "unassisted";
}

// Host pede um ID único de 6 dígitos ao servidor (envia nome do dispositivo e SO).
public sealed record RegisterHostRequest(string DeviceName, string Os);
public sealed record RegisterHostResult(bool Ok, string? HostId, string? Error);

// Host informa que está online (disponível para conexão), com porta de escuta e modo de acesso.
// AccessMode: "assisted" ou "unassisted" (veja HostAccess acima).
// Salt/Verifier são obrigatórios quando AccessMode == "unassisted" (senha do host definida pelo usuário,
// hasheada com PasswordHasher do Core — o App gera salt e verifier localmente e envia base64).
// Salt/Verifier devem ser null quando "assisted".
public sealed record HostOnlineRequest(string HostId, string DeviceName, string Os, int ListenPort, string AccessMode, string? Salt, string? Verifier);
public sealed record HostOnlineResult(bool Ok, string? Error);

// Cliente pede o salt do host para computar o AuthHash (apenas no modo unassisted).
public sealed record GetHostSaltRequest(string HostId);
public sealed record GetHostSaltResult(bool Ok, string? AccessMode, string? Salt, string? Error);

// Cliente solicita conexão: informa o HostId e, se unassisted, o AuthHash (PBKDF2 da senha com o salt obtido).
// Se assisted, AuthHash pode ser null e o servidor notificará o host para aprovação manual.
public sealed record LookupRequest(string HostId, string? AuthHash);
public sealed record LookupResult(bool Ok, string? Ip, int Port, string? SessionToken, string? Error);

// Servidor notifica o HOST que alguém quer conectar (via conexão de controle do host).
// RequiresApproval == true → modo assistido: o usuário do host decide; false → não assistido: aceite automático.
public sealed record ConnectNotify(string SessionToken, string ClientName, string ClientOs, bool RequiresApproval);
// Host responde ao servidor: aceita ou rejeita a conexão.
public sealed record ConnectAck(string HostId, bool Accepted, int ListenPort);
