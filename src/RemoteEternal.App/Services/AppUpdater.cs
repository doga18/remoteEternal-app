using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteEternal.App.Services;

/// <summary>
/// Baixa, valida e aplica uma atualização (ZIP self-contained) do endpoint da API.
/// A atualização é aplicada numa pasta nova e o app é reiniciado apontando para ela.
/// </summary>
public static class AppUpdater
{
    /// <summary>
    /// Baixa e aplica a atualização, retornando true se o processo de reinício foi
    /// iniciado. Em caso de falha, retorna false (o app continua na versão atual).
    /// </summary>
    public static async Task<bool> ApplyAsync(UpdateInfo update, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string currentVersion = AppState.AppVersion;
            string updatesRoot = Path.Combine(baseDir, "..", "updates");
            string versionDir = Path.Combine(updatesRoot, "v" + update.Version);
            string zipPath = Path.Combine(Path.GetTempPath(), $"RemoteEternal-{update.Version}.zip");

            progress?.Report($"Baixando {update.Version} ({update.SizeBytes / 1_000_000.0:0.0} MB)...");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            // Validação opcional de integridade (SHA256).
            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                progress?.Report("Validando integridade...");
                string actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zipPath);
                    progress?.Report("Falha na validação de integridade (SHA256 divergente).");
                    return false;
                }
            }

            // Extrai para a pasta da nova versão.
            progress?.Report("Extraindo...");
            if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
            Directory.CreateDirectory(versionDir);
            ZipFile.ExtractToDirectory(zipPath, versionDir);
            File.Delete(zipPath);

            string newExe = Path.Combine(versionDir, "RemoteEternal.exe");
            if (!File.Exists(newExe))
            {
                progress?.Report("Atualização extraída, mas RemoteEternal.exe não foi encontrado.");
                return false;
            }

            // Reinicia o app apontando para o novo executável.
            progress?.Report("Reiniciando...");
            string args = $"--updated-from={currentVersion}";
            Process.Start(new ProcessStartInfo(newExe, args) { WorkingDirectory = versionDir, UseShellExecute = true });
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report("Falha ao aplicar atualização: " + ex.Message);
            DiagnosticLog.Write("AppUpdater", "Falha: " + ex.Message);
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}