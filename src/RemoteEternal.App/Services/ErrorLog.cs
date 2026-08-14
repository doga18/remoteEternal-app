using System.IO;

namespace RemoteEternal.App.Services;

/// <summary>
/// Grava erros e avisos de diagnóstico em
/// <c>%APPDATA%\RemoteEternal\error.log</c>.
///
/// A escrita nunca lança: se o próprio log falhar (ex.: sem permissão), a falha
/// é silenciosa para não mascarar nem impedir a exceção original de ser reportada.
/// </summary>
public static class ErrorLog
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteEternal", "error.log");

    /// <summary>Grava uma linha com timestamp UTC seguida do conteúdo formatado.</summary>
    public static void Write(string message)
    {
        try
        {
            string dir = Path.GetDirectoryName(LogPath) ?? "";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}] {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            // Nunca falhe ao logar.
        }
    }

    /// <summary>Grava um contexto e a exceção completa (ToString).</summary>
    public static void Write(Exception ex, string context)
    {
        Write(context + Environment.NewLine + ex);
    }
}
