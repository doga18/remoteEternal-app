using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.App.Services;

public sealed record UpdateInfo(string Version, string Url, string Notes, long SizeBytes, string Sha256, int FileCount);

public sealed class ServerConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonUtil.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private HttpClient? _http;
    private ClientWebSocket? _hostWs;
    private CancellationTokenSource? _cts;
    private Task? _wsReadTask;
    private int _disconnectedRaised;
    private bool _disposed;

    public string? ApiUrl { get; private set; }
    public bool IsConnected => _http is not null && !_disposed;

    public event Action? Disconnected;
    public event Action<ConnectNotify>? ConnectNotifyReceived;
    public event Action? HostWsClosed;

    public async Task ConnectAsync(string apiUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(baseUri.Host))
            throw new ArgumentException("URL da API inválida. Use http:// ou https://.", nameof(apiUrl));

        string normalized = baseUri.ToString().TrimEnd('/') + "/";
        var http = new HttpClient { BaseAddress = new Uri(normalized), Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await http.GetAsync("api/health", ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var health = Deserialize<JsonElement>(body);
            if (!response.IsSuccessStatusCode || !health.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new IOException("Não foi possível conectar à API.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            http.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            http.Dispose();
            throw new IOException("Não foi possível conectar à API.", ex);
        }

        await DisposeHttpAndWsAsync().ConfigureAwait(false);
        _http = http;
        ApiUrl = normalized;
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _disconnectedRaised, 0);
    }

    public Task<RegisterHostResult> RegisterHostAsync(string deviceName, string os) =>
        HttpPostAsync<RegisterHostResult>("api/hosts/register", new { deviceName, os });

    public Task<HostOnlineResult> HostOnlineAsync(string hostId, string deviceName, string os,
        int listenPort, string accessMode, string? saltB64, string? verifierB64, string? advertisedAddress = null) =>
        HttpPostAsync<HostOnlineResult>("api/hosts/online",
            new { hostId, deviceName, os, listenPort, accessMode, salt = saltB64, verifier = verifierB64, advertisedAddress });

    public Task<GetHostSaltResult> GetHostSaltAsync(string hostId) =>
        HttpGetAsync<GetHostSaltResult>($"api/hosts/{Uri.EscapeDataString(hostId)}/salt");

    public async Task<LookupResult> LookupAsync(string hostId, string? authHash)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await HttpPostAsync<LookupResult>($"api/hosts/{Uri.EscapeDataString(hostId)}/lookup",
            new { authHash }, timeout.Token).ConfigureAwait(false);
    }

    public async Task<UpdateInfo?> GetLatestUpdateAsync(string currentVersion)
    {
        var response = await HttpGetAsync<LatestUpdateResponse>(
            $"api/update/latest?currentVersion={Uri.EscapeDataString(currentVersion)}").ConfigureAwait(false);
        if (!response.Ok || response.Update is null) return null;
        return new UpdateInfo(
            response.Update.Version ?? "",
            response.Update.Url ?? "",
            response.Update.Notes ?? "",
            response.Update.SizeBytes ?? 0,
            response.Update.Sha256 ?? "",
            response.Update.FileCount ?? 0);
    }

    public async Task HostWsConnectAsync(string hostId, CancellationToken ct = default)
    {
        if (ApiUrl is null || _cts is null) throw new IOException("Não conectado à API");
        await HostWsCloseAsync().ConfigureAwait(false);
        var ws = new ClientWebSocket();
        UriBuilder builder = new(ApiUrl) { Scheme = ApiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/ws" };
        try
        {
            await ws.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
            _hostWs = ws;
            await SendWsAsync(ws, new { type = "hello", data = new { hostId } }, ct).ConfigureAwait(false);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var hello = await ReceiveWsMessageAsync(ws, helloTimeout.Token).ConfigureAwait(false);
            if (hello is null || hello.Value.GetProperty("type").GetString() != "helloResult" ||
                !hello.Value.GetProperty("data").GetProperty("ok").GetBoolean())
                throw new IOException("A API recusou a conexão WebSocket do host.");
            _wsReadTask = Task.Run(() => HostWsReadLoopAsync(ws, _cts.Token));
        }
        catch
        {
            await CloseWebSocketAsync(ws).ConfigureAwait(false);
            if (ReferenceEquals(_hostWs, ws)) _hostWs = null;
            throw;
        }
    }

    public async Task SendConnectAckWsAsync(string hostId, bool accepted, int listenPort)
    {
        var ws = _hostWs;
        if (ws is null || ws.State != WebSocketState.Open) throw new IOException("Canal WebSocket do host não está conectado.");
        await SendWsAsync(ws, new { type = "connectAck", data = new { hostId, accepted, listenPort } }, _cts?.Token ?? default).ConfigureAwait(false);
    }

    public async Task HostWsCloseAsync()
    {
        var ws = Interlocked.Exchange(ref _hostWs, null);
        if (ws is not null) await CloseWebSocketAsync(ws).ConfigureAwait(false);
        _wsReadTask = null;
    }

    private async Task HostWsReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await ReceiveWsMessageAsync(ws, ct).ConfigureAwait(false);
                if (message is null) break;
                var root = message.Value;
                if (root.GetProperty("type").GetString() != "connectNotify" || !root.TryGetProperty("data", out var data)) continue;
                var notify = data.Deserialize<ConnectNotify>(JsonOptions);
                if (notify is not null) ConnectNotifyReceived?.Invoke(notify);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            if (ReferenceEquals(_hostWs, ws))
            {
                _hostWs = null;
                HostWsClosed?.Invoke();
            }
        }
    }

    private async Task<T> HttpGetAsync<T>(string path, CancellationToken ct = default)
    {
        return await SendHttpAsync<T>(() => _http!.GetAsync(path, ct), ct).ConfigureAwait(false);
    }

    private async Task<T> HttpPostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(body, JsonOptions);
        return await SendHttpAsync<T>(() => _http!.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), ct), ct).ConfigureAwait(false);
    }

    private async Task<T> SendHttpAsync<T>(Func<Task<HttpResponseMessage>> request, CancellationToken ct)
    {
        if (_http is null || _disposed) throw new IOException("Não conectado à API");
        try
        {
            using var response = await request().ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                body = JsonSerializer.Serialize(new { ok = false, error = "Muitas tentativas. Tente novamente em instantes" }, JsonOptions);
            var result = Deserialize<T>(body);
            if (result is null) throw new IOException("Resposta inválida da API.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { RaiseDisconnected(); throw new IOException("Tempo esgotado ao comunicar com a API."); }
        catch (HttpRequestException ex) { RaiseDisconnected(); throw new IOException("Falha ao comunicar com a API.", ex); }
    }

    private static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new JsonException("Resposta vazia");

    private static async Task<JsonElement?> ReceiveWsMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.Count > 0) ms.Write(buffer, 0, result.Count);
            if (ms.Length > 16 * 1024) throw new IOException("Mensagem WebSocket excede o limite.");
        } while (!result.EndOfMessage);
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    private async Task SendWsAsync(ClientWebSocket ws, object message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _wsSendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _wsSendLock.Release(); }
    }

    private static async Task CloseWebSocketAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "encerrando", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        ws.Dispose();
    }

    private async Task DisposeHttpAndWsAsync()
    {
        await HostWsCloseAsync().ConfigureAwait(false);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _http?.Dispose();
        _http = null;
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0) Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeHttpAndWsAsync().ConfigureAwait(false);
        _wsSendLock.Dispose();
    }

    private sealed record LatestUpdateResponse(bool Ok, LatestUpdate? Update, string? Error);
    private sealed record LatestUpdate(string? Version, string? Url, string? Notes, long? SizeBytes, string? Sha256, int? FileCount);
}
