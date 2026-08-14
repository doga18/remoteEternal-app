using System.IO;
using System.Reflection;
using FFmpeg.AutoGen;
using RemoteEternal.App.Services;

namespace RemoteEternal.App.Media;

/// <summary>
/// Carrega as DLLs nativas FFmpeg necessárias ao FFmpeg.AutoGen.
///
/// Em publicações single-file, as DLLs ficam embutidas como recursos
/// (nome lógico "ffmpeg.&lt;arquivo&gt;.dll") e são extraídas para
/// <see cref="FfmpegPath"/> na primeira execução. Em builds de desenvolvimento,
/// a pasta ffmpeg é copiada para a saída pelo csproj e usada diretamente.
/// </summary>
public static class FfmpegLibrary
{
    private const string EmbeddedPrefix = "ffmpeg.";
    private static bool _initialized;

    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static void EnsureLoaded()
    {
        if (_initialized) return;
        string path = FfmpegPath;
        try
        {
            ExtractEmbeddedIfNeeded(path);
        }
        catch (Exception ex)
        {
            // Falha ao extrair as DLLs embutidas (ex.: pasta sem permissão de escrita).
            // Não derruba o app: registra o aviso em %APPDATA%\RemoteEternal\error.log
            // e segue sem FFmpeg — o cliente mostra "vídeo indisponível" no ViewerWindow.
            // Sessão de controle (host/cliente) não depende de FFmpeg.
            ErrorLog.Write(ex, "Aviso: falha ao extrair DLLs FFmpeg");
            return;
        }
        if (Directory.Exists(path))
        {
            ffmpeg.RootPath = path;
            var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", path + ";" + processPath);
        }
        DynamicallyLoadedBindings.Initialize();
        _initialized = true;
    }

    /// <summary>
    /// Extrai as DLLs FFmpeg embutidas para a pasta <paramref name="dir"/> quando
    /// a pasta não existir ou estiver incompleta. Não faz nada quando não há
    /// recursos embutidos (ex.: build de desenvolvimento, onde o csproj copia a pasta).
    /// </summary>
    private static void ExtractEmbeddedIfNeeded(string dir)
    {
        var assembly = typeof(FfmpegLibrary).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
            .ToList();
        if (names.Count == 0) return;

        if (IsComplete(dir, names)) return;

        Directory.CreateDirectory(dir);
        foreach (var name in names)
        {
            var fileName = name.Substring(EmbeddedPrefix.Length);
            var dest = Path.Combine(dir, fileName);
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(file);
        }
    }

    private static bool IsComplete(string dir, IReadOnlyCollection<string> names)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var name in names)
        {
            var fileName = name.Substring(EmbeddedPrefix.Length);
            if (!File.Exists(Path.Combine(dir, fileName))) return false;
        }
        return true;
    }
}
