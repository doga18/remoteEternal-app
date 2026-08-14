using RemoteEternal.Server;

int port = 7000;
string dbPath = Path.Combine(AppContext.BaseDirectory, "remoteeternal.db");
bool allowRegistration = true;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
            port = p;
            i++;
            break;
        case "--db" when i + 1 < args.Length:
            dbPath = args[i + 1];
            i++;
            break;
        case "--no-register":
            allowRegistration = false;
            break;
        default:
            Console.WriteLine("Uso: RemoteEternal.Server [--port 7000] [--db arquivo.db] [--no-register]");
            return;
    }
}

var server = new RemoteEternalServer(port, dbPath, allowRegistration);
await server.RunAsync();
