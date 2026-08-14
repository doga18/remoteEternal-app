using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteEternal.App.Services;

/// <summary>
/// Resolve o IPv4 local que o cliente alcançável usa para falar com a API (e,
/// por extensão, o endereço que deve ser anunciado no plano de controle para a
/// sessão direta). O resultado é enviado somente no request autenticado de
/// anúncio do host; nunca é logado nem exibido na UI.
/// </summary>
public static class NetworkAddressResolver
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Obtém o IPv4 local usado para alcançar <paramref name="apiUrl"/>.
    /// Primeiro tenta uma conexão TCP curta e sem dados até o host/porta da API
    /// e usa o <c>LocalEndPoint</c>; se falhar ou não produzir um endereço
    /// anunciável, enumera as interfaces de rede. Retorna null quando nenhum
    /// endereço IPv4 utilizável existe.
    /// </summary>
    public static async Task<string?> ResolveAsync(string apiUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
            return null;

        int port = uri.IsDefaultPort
            ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80)
            : uri.Port;

        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(uri.Host, port, timeout.Token).ConfigureAwait(false);
            if (tcp.Client.LocalEndPoint is IPEndPoint local &&
                TryGetIpv4(local.Address, out var ip))
                return ip.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Conectar só para descobrir a rota é heurística; a falha cai no fallback.
        }

        return EnumerateInterfaceAddress();
    }

    private static string? EnumerateInterfaceAddress()
    {
        var up = new List<IPAddress>();
        var rest = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (TryGetIpv4(unicast.Address, out var ip)) addresses.Add(ip);
                }
            }
            catch
            {
                // Interface sem propriedades legíveis; continua para as demais.
            }
            (ni.OperationalStatus == OperationalStatus.Up ? up : rest).AddRange(addresses);
        }
        return (up.FirstOrDefault() ?? rest.FirstOrDefault())?.ToString();
    }

    /// <summary>Converte IPv4-mapped-IPv6 e valida se o endereço é anunciável.</summary>
    private static bool TryGetIpv4(IPAddress address, out IPAddress ipv4)
    {
        ipv4 = address;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            ipv4 = address.MapToIPv4();
        return ipv4.AddressFamily == AddressFamily.InterNetwork && IsValidAdvertisedIpv4(ipv4);
    }

    private static bool IsValidAdvertisedIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(ip)) return false;              // 127.0.0.0/8
        if (ip.Equals(IPAddress.Any)) return false;              // 0.0.0.0
        if (ip.Equals(IPAddress.Broadcast)) return false;        // 255.255.255.255
        byte[] b = ip.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return false;            // 169.254.0.0/16 link-local
        if (b[0] >= 224 && b[0] <= 239) return false;            // 224.0.0.0/4 multicast
        return true;
    }
}
