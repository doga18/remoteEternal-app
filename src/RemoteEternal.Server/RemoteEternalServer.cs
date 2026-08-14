using System.Net;
using System.Net.Sockets;

namespace RemoteEternal.Server;

public class RemoteEternalServer
{
    public HostStore Hosts { get; }
    public ClientRegistry Registry { get; } = new();
    public RateLimiter RateLimiter { get; } = new();
    public bool AllowRegistration { get; }
    public int Port { get; }
    public string DbPath { get; }

    private readonly TcpListener _listener;

    public RemoteEternalServer(int port, string dbPath, bool allowRegistration)
    {
        Port = port;
        DbPath = dbPath;
        AllowRegistration = allowRegistration;
        Hosts = new HostStore(dbPath);
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _listener.Start();
        Console.WriteLine($"RemoteEternal Server ouvindo na porta {Port}");
        Console.WriteLine($"Banco de dados: {DbPath}");
        Console.WriteLine("Registro de novos hosts: " + (AllowRegistration ? "HABILITADO" : "desabilitado"));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var session = new ClientSession(tcp, this);
                _ = Task.Run(() => session.RunAsync(), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }
}
