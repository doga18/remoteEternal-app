using System.IO;

namespace RemoteEternal.App.Services;

/// <summary>
/// Diagnóstico observacional em tempo real (somente leitura de eventos de sessão).
///
/// Nunca registra token, senha, verifier, IP, payload, chaves ou nomes de monitor
/// completos: apenas estados, tipos de frame, contagens, tamanhos em bytes e
/// mensagens de exceção sanitizadas (as mesmas já usadas na UI/ErrorLog).
///
/// As linhas são exibidas em tempo real via <see cref="LineWritten"/> (que pode ser
/// disparado de qualquer thread; a UI deve usar o Dispatcher) e também são persistidas
/// em <c>%APPDATA%\RemoteEternal\debug.log</c> para diagnóstico mesmo sem UI aberta.
/// A escrita nunca lança (mesmo padrão de segurança do <see cref="ErrorLog"/>).
/// </summary>
public static class DiagnosticLog
{
    /// <summary>Disparado a cada linha escrita. Pode vir de threads de sessão; a UI
    /// deve encaminhar via Dispatcher. Um handler que lance não interrompe o diagnóstico.</summary>
    public static event Action<string>? LineWritten;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteEternal", "debug.log");

    /// <summary>Grava uma linha com timestamp UTC, categoria e mensagem sanitizada.</summary>
    public static void Write(string category, string message)
    {
        string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}] [{category}] {message}";
        try
        {
            string dir = Path.GetDirectoryName(LogPath) ?? "";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Nunca falhe ao logar (mesmo padrão do ErrorLog).
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // Um handler de UI não pode quebrar o diagnóstico.
        }
    }
}
