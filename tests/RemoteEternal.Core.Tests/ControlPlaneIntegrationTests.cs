using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemoteEternal.Core.Auth;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;
using Xunit;

namespace RemoteEternal.Core.Tests;

/// <summary>
/// Testes de integração do plano de controle por ID (estilo TeamViewer).
///
/// Cada teste sobe um <see cref="RemoteEternal.Server.RemoteEternalServer"/> in-process
/// em porta alta aleatória com banco LiteDB temporário e usa clientes TCP brutos
/// (TcpClient + FrameChannel + EnvelopeUtil), sem WPF.
///
/// O RateLimiter é por instância do servidor; como cada teste usa um servidor próprio,
/// o bloqueio de 127.0.0.1 em um teste não vaza para os demais. O teste de rate limit
/// (5 falhas → bloqueio) é, portanto, isolado naturalmente.
/// </summary>
public sealed class ControlPlaneIntegrationTests
{
    [Fact]
    [Trait("Category", "integration")]
    public async Task ControlPlane_RegisterHost_ReturnsUniqueSixDigitIds()
    {
        await using var harness = await ControlPlaneHarness.StartAsync();
        var ct = harness.Token;

        var host1 = await harness.ConnectAsync();
        var reg1 = await host1.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-PC", "Windows 11"), MsgTypes.RegisterHostResult, ct);
        Assert.NotNull(reg1);
        Assert.True(reg1!.Ok, reg1.Error);
        Assert.NotNull(reg1.HostId);
        Assert.Matches("^[0-9]{6}$", reg1.HostId!);
        Assert.InRange(int.Parse(reg1.HostId!), 100_000, 999_999);

        // Segunda conexão registra outro host com ID diferente (unicidade).
        var host2 = await harness.ConnectAsync();
        var reg2 = await host2.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-Laptop", "Windows 10"), MsgTypes.RegisterHostResult, ct);
        Assert.NotNull(reg2);
        Assert.True(reg2!.Ok, reg2.Error);
        Assert.NotNull(reg2.HostId);
        Assert.Matches("^[0-9]{6}$", reg2.HostId!);
        Assert.NotEqual(reg1.HostId, reg2.HostId);
    }

    [Fact]
    [Trait("Category", "integration")]
    public async Task ControlPlane_UnassistedHost_LookupValidatesPasswordAndCompletes()
    {
        await using var harness = await ControlPlaneHarness.StartAsync();
        var ct = harness.Token;

        var host = await harness.ConnectAsync();
        var reg = await host.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-PC", "Windows 11"), MsgTypes.RegisterHostResult, ct);
        Assert.True(reg!.Ok, reg.Error);
        string hostId = reg.HostId!;

        var salt = PasswordHasher.GenerateSalt();
        var salt64 = Convert.ToBase64String(salt);
        var verifier64 = Convert.ToBase64String(PasswordHasher.Compute(salt, "senha123"));
        var online = await host.Request<HostOnlineResult>(MsgTypes.HostOnline,
            new HostOnlineRequest(hostId, "QA-PC", "Windows 11", 5051, HostAccess.Unassisted, salt64, verifier64),
            MsgTypes.HostOnlineResult, ct);
        Assert.True(online!.Ok, online.Error);

        // Host responde ConnectAck automaticamente (modo não assistido).
        var notifyTcs = new TaskCompletionSource<ConnectNotify>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = host.RunHostResponderAsync(_ => new ConnectAck(hostId, true, 5051), n => notifyTcs.TrySetResult(n), ct);

        var client = await harness.ConnectAsync();
        var saltResult = await client.Request<GetHostSaltResult>(MsgTypes.GetHostSalt, new GetHostSaltRequest(hostId), MsgTypes.GetHostSaltResult, ct);
        Assert.NotNull(saltResult);
        Assert.True(saltResult!.Ok, saltResult.Error);
        Assert.Equal(HostAccess.Unassisted, saltResult.AccessMode);
        Assert.Equal(salt64, saltResult.Salt);

        // Senha errada → erro (1 falha no RateLimiter, abaixo do limite de 5).
        var wrong = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, PasswordHasher.ComputeBase64(salt64, "senha-errada")), MsgTypes.LookupResult, ct);
        Assert.NotNull(wrong);
        Assert.False(wrong!.Ok);
        Assert.Contains("incorreta", wrong.Error!);

        // Senha correta → servidor notifica o host (RequiresApproval=false) e completa.
        var good = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, PasswordHasher.ComputeBase64(salt64, "senha123")), MsgTypes.LookupResult, ct);
        Assert.NotNull(good);
        Assert.True(good!.Ok, good.Error);
        Assert.False(string.IsNullOrEmpty(good.Ip));
        Assert.Equal(5051, good.Port);
        Assert.False(string.IsNullOrEmpty(good.SessionToken));

        var notify = await notifyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(notify.RequiresApproval);
    }

    [Fact]
    [Trait("Category", "integration")]
    public async Task ControlPlane_AssistedHost_RejectThenAccept()
    {
        await using var harness = await ControlPlaneHarness.StartAsync();
        var ct = harness.Token;

        var host = await harness.ConnectAsync();
        var reg = await host.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-Assisted", "Windows 11"), MsgTypes.RegisterHostResult, ct);
        Assert.True(reg!.Ok, reg.Error);
        string hostId = reg.HostId!;

        var online = await host.Request<HostOnlineResult>(MsgTypes.HostOnline,
            new HostOnlineRequest(hostId, "QA-Assisted", "Windows 11", 5052, HostAccess.Assisted, null, null),
            MsgTypes.HostOnlineResult, ct);
        Assert.True(online!.Ok, online.Error);

        // Host decide manualmente: primeiro recusa, depois aceita.
        var acks = new Queue<ConnectAck>();
        acks.Enqueue(new ConnectAck(hostId, false, 0));
        acks.Enqueue(new ConnectAck(hostId, true, 5052));
        var notifies = new ConcurrentQueue<ConnectNotify>();
        var responder = host.RunHostResponderAsync(_ => acks.Dequeue(), n => notifies.Enqueue(n), ct);

        var client = await harness.ConnectAsync();
        var saltResult = await client.Request<GetHostSaltResult>(MsgTypes.GetHostSalt, new GetHostSaltRequest(hostId), MsgTypes.GetHostSaltResult, ct);
        Assert.NotNull(saltResult);
        Assert.True(saltResult!.Ok, saltResult.Error);
        Assert.Equal(HostAccess.Assisted, saltResult.AccessMode);
        Assert.Null(saltResult.Salt);

        // Cenário rejeição.
        var rejected = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, null), MsgTypes.LookupResult, ct);
        Assert.NotNull(rejected);
        Assert.False(rejected!.Ok);
        Assert.Contains("recusada", rejected.Error!);

        // Cenário aceite (nova solicitação).
        var accepted = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, null), MsgTypes.LookupResult, ct);
        Assert.NotNull(accepted);
        Assert.True(accepted!.Ok, accepted.Error);
        Assert.False(string.IsNullOrEmpty(accepted.Ip));
        Assert.Equal(5052, accepted.Port);
        Assert.False(string.IsNullOrEmpty(accepted.SessionToken));

        Assert.Equal(2, notifies.Count);
        Assert.All(notifies, n => Assert.True(n.RequiresApproval));
    }

    [Fact]
    [Trait("Category", "integration")]
    public async Task ControlPlane_NegativeScenarios()
    {
        await using var harness = await ControlPlaneHarness.StartAsync();
        var ct = harness.Token;

        var client = await harness.ConnectAsync();

        // getHostSalt de ID inexistente.
        var unknownSalt = await client.Request<GetHostSaltResult>(MsgTypes.GetHostSalt, new GetHostSaltRequest("999999"), MsgTypes.GetHostSaltResult, ct);
        Assert.NotNull(unknownSalt);
        Assert.False(unknownSalt!.Ok);
        Assert.Contains("não encontrado", unknownSalt.Error!);

        // lookup de ID inexistente/offline (1 falha no RateLimiter).
        var offline = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest("999999", "auth-hash"), MsgTypes.LookupResult, ct);
        Assert.NotNull(offline);
        Assert.False(offline!.Ok);
        Assert.Contains("offline", offline.Error!);

        // lookup sem senha em modo unassisted (2ª falha no RateLimiter).
        var host = await harness.ConnectAsync();
        var reg = await host.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-PC", "Windows 11"), MsgTypes.RegisterHostResult, ct);
        Assert.True(reg!.Ok, reg.Error);
        string hostId = reg.HostId!;
        var salt = PasswordHasher.GenerateSalt();
        var online = await host.Request<HostOnlineResult>(MsgTypes.HostOnline,
            new HostOnlineRequest(hostId, "QA-PC", "Windows 11", 5051, HostAccess.Unassisted,
                Convert.ToBase64String(salt), Convert.ToBase64String(PasswordHasher.Compute(salt, "senha123"))),
            MsgTypes.HostOnlineResult, ct);
        Assert.True(online!.Ok, online.Error);

        var noPassword = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, null), MsgTypes.LookupResult, ct);
        Assert.NotNull(noPassword);
        Assert.False(noPassword!.Ok);
        Assert.Contains("incorreta", noPassword.Error!);
    }

    [Fact]
    [Trait("Category", "integration")]
    public async Task ControlPlane_RateLimiter_BlocksAfterFiveFailures()
    {
        await using var harness = await ControlPlaneHarness.StartAsync();
        var ct = harness.Token;

        var host = await harness.ConnectAsync();
        var reg = await host.Request<RegisterHostResult>(MsgTypes.RegisterHost, new RegisterHostRequest("QA-PC", "Windows 11"), MsgTypes.RegisterHostResult, ct);
        Assert.True(reg!.Ok, reg.Error);
        string hostId = reg.HostId!;
        var salt = PasswordHasher.GenerateSalt();
        var salt64 = Convert.ToBase64String(salt);
        var online = await host.Request<HostOnlineResult>(MsgTypes.HostOnline,
            new HostOnlineRequest(hostId, "QA-PC", "Windows 11", 5051, HostAccess.Unassisted,
                salt64, Convert.ToBase64String(PasswordHasher.Compute(salt, "senha123"))),
            MsgTypes.HostOnlineResult, ct);
        Assert.True(online!.Ok, online.Error);
        var responder = host.RunHostResponderAsync(_ => new ConnectAck(hostId, true, 5051), _ => { }, ct);

        var client = await harness.ConnectAsync();
        for (int i = 0; i < 5; i++)
        {
            var fail = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, PasswordHasher.ComputeBase64(salt64, "senha-errada")), MsgTypes.LookupResult, ct);
            Assert.NotNull(fail);
            Assert.False(fail!.Ok);
            Assert.Contains("incorreta", fail.Error!);
        }

        // A 6ª tentativa (mesmo com senha correta) é bloqueada por IP.
        var blocked = await client.Request<LookupResult>(MsgTypes.Lookup, new LookupRequest(hostId, PasswordHasher.ComputeBase64(salt64, "senha123")), MsgTypes.LookupResult, ct);
        Assert.NotNull(blocked);
        Assert.False(blocked!.Ok);
        Assert.Contains("Muitas tentativas", blocked.Error!);
    }

    private sealed class ControlPlaneHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _run;
        private readonly List<RawClient> _clients = new();
        private readonly List<Task> _background = new();
        public RemoteEternal.Server.RemoteEternalServer Server { get; }
        public string DbPath { get; }
        public CancellationToken Token => _cts.Token;

        private ControlPlaneHarness(RemoteEternal.Server.RemoteEternalServer server, Task run, string dbPath, CancellationTokenSource cts)
        {
            Server = server;
            _run = run;
            DbPath = dbPath;
            _cts = cts;
        }

        public static async Task<ControlPlaneHarness> StartAsync()
        {
            Exception? last = null;
            for (var i = 0; i < 8; i++)
            {
                var port = Random.Shared.Next(20000, 50000);
                var db = Path.Combine(Path.GetTempPath(), "remote-eternal-control-" + Guid.NewGuid().ToString("N") + ".db");
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    var server = new RemoteEternal.Server.RemoteEternalServer(port, db, true);
                    var run = server.RunAsync(cts.Token);
                    var harness = new ControlPlaneHarness(server, run, db, cts);
                    await harness.ConnectAsync(); // garante que o listener está aceitando
                    return harness;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { cts.Cancel(); } catch { }
                    CoreProtocolTests.TryDelete(db);
                }
            }
            throw new IOException("Could not start integration server", last);
        }

        public async Task<RawClient> ConnectAsync()
        {
            Exception? last = null;
            for (var i = 0; i < 60; i++)
            {
                var tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, Server.Port, Token);
                    var client = new RawClient(tcp);
                    lock (_clients) _clients.Add(client);
                    return client;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    last = ex;
                    tcp.Dispose();
                    await Task.Delay(50, Token);
                }
            }
            throw new IOException("Could not connect to integration server", last);
        }

        public void Track(Task task)
        {
            lock (_background) _background.Add(task);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            lock (_clients) { foreach (var c in _clients) c.Dispose(); }
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try
            {
                lock (_background) { Task.WaitAll(_background.ToArray(), TimeSpan.FromSeconds(2)); }
            }
            catch { }
            CoreProtocolTests.TryDelete(DbPath);
            _cts.Dispose();
        }
    }

    private sealed class RawClient : IDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly SemaphoreSlim sendLock = new(1, 1);

        public RawClient(TcpClient tcp)
        {
            this.tcp = tcp;
            stream = tcp.GetStream();
        }

        public async Task<T?> Request<T>(string type, object data, string expected, CancellationToken ct)
        {
            await Send(type, data, ct);
            while (true)
            {
                var env = EnvelopeUtil.Parse(await FrameChannel.ReadFrameAsync(stream, ct));
                if (env.Type == expected) return EnvelopeUtil.Data<T>(env);
            }
        }

        public async Task Send(string type, object data, CancellationToken ct)
        {
            await sendLock.WaitAsync(ct);
            try { await FrameChannel.WriteFrameAsync(stream, EnvelopeUtil.Create(type, data), ct); }
            finally { sendLock.Release(); }
        }

        /// <summary>
        /// Loop de leitura do "host de teste": responde ConnectNotify com ConnectAck
        /// conforme <paramref name="ackFactory"/> e observa notificações via <paramref name="onNotify"/>.
        /// Encerra silenciosamente quando o token é cancelado ou a conexão fecha.
        /// </summary>
        public async Task RunHostResponderAsync(Func<ConnectNotify, ConnectAck> ackFactory, Action<ConnectNotify>? onNotify, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var env = EnvelopeUtil.Parse(await FrameChannel.ReadFrameAsync(stream, ct));
                    if (env.Type != MsgTypes.ConnectNotify) continue;
                    var notify = EnvelopeUtil.Data<ConnectNotify>(env);
                    if (notify is null) continue;
                    onNotify?.Invoke(notify);
                    await Send(MsgTypes.ConnectAck, ackFactory(notify), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            try { stream.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            sendLock.Dispose();
        }
    }
}
