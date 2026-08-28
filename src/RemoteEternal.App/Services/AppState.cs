using System.IO;

namespace RemoteEternal.App.Services;

public static class AppState
{
    /// <summary>Versão enviada ao endpoint de atualização; altere ao publicar uma nova versão.</summary>
    public const string AppVersion = "1.0.5";
    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteEternal");

    private static string ConfigFile => Path.Combine(ConfigDir, "config.txt");

    public static string DeviceId { get; }
    public static string DeviceName { get; }
    public static string Os { get; }

    /// <summary>ID público de 6 dígitos do host, atribuído pelo servidor e persistido localmente.</summary>
    public static string HostId { get; private set; } = "";

    public static string ApiUrl { get; set; } = "https://remoteeternal-api.onrender.com";
    public static int ListenPort { get; set; } = 5050;
    public static string LastUsername { get; set; } = "";

    static AppState()
    {
        DeviceName = Environment.MachineName;
        Os = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        Directory.CreateDirectory(ConfigDir);
        string idFile = Path.Combine(ConfigDir, "device.id");
        if (File.Exists(idFile))
        {
            DeviceId = File.ReadAllText(idFile).Trim();
            if (string.IsNullOrEmpty(DeviceId)) DeviceId = CreateNewId(idFile);
        }
        else
        {
            DeviceId = CreateNewId(idFile);
        }
        string hostIdFile = Path.Combine(ConfigDir, "host.id");
        if (File.Exists(hostIdFile))
        {
            string saved = File.ReadAllText(hostIdFile).Trim();
            if (!string.IsNullOrEmpty(saved)) HostId = saved;
        }
        Load();
    }

    /// <summary>Persiste o ID do host em host.id. Nunca persiste senha ou verifier.</summary>
    public static void SaveHostId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        HostId = id.Trim();
        try
        {
            File.WriteAllText(Path.Combine(ConfigDir, "host.id"), HostId);
        }
        catch
        {
        }
    }

    private static string CreateNewId(string path)
    {
        string id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        File.WriteAllText(path, id);
        return id;
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            foreach (string line in File.ReadAllLines(ConfigFile))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string key = line[..i].Trim();
                string value = line[(i + 1)..].Trim();
                switch (key)
                {
                    case "apiUrl": ApiUrl = value; break;
                    case "listenPort": if (int.TryParse(value, out var lp)) ListenPort = lp; break;
                    case "username": LastUsername = value; break;
                }
            }
        }
        catch
        {
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllLines(ConfigFile, new[]
            {
                $"apiUrl={ApiUrl}",
                $"listenPort={ListenPort}",
                $"username={LastUsername}"
            });
        }
        catch
        {
        }
    }
}
