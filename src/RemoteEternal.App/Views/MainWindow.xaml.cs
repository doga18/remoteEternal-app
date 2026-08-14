using System.Windows;
using System.Windows.Controls;
using RemoteEternal.App.Services;
using RemoteEternal.Core.Auth;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.App.Views;

public partial class MainWindow : Window
{
    private ServerConnection? _conn;
    private readonly SessionHost _host = new();
    private bool _hostActive;

    public MainWindow()
    {
        InitializeComponent();
        TxtServer.Text = AppState.ServerAddress;
        TxtPort.Text = AppState.ServerPort.ToString();
        UpdateHostPasswordVisibility();
        _host.StatusChanged += msg => Dispatcher.Invoke(() => TxtHostStatus.Text = msg);
        _host.SessionActiveChanged += active => Dispatcher.Invoke(() =>
        {
            if (active) TxtHostStatus.Text = "Sessão remota ATIVA";
        });
    }

    private async void OnConnectServerClick(object sender, RoutedEventArgs e)
    {
        string server = TxtServer.Text.Trim();
        if (string.IsNullOrEmpty(server)) { ShowConnectError("Informe o endereço do servidor"); return; }
        if (!int.TryParse(TxtPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowConnectError("Porta inválida");
            return;
        }
        try
        {
            SetConnectBusy(true);
            if (_conn is not null) await _conn.DisposeAsync();
            _conn = null;

            var conn = new ServerConnection();
            conn.Disconnected += () => Dispatcher.Invoke(() =>
            {
                _hostActive = false;
                BtnToggleHost.Content = "Iniciar acesso";
                TxtHostId.Text = "—";
                UpdateHostPasswordVisibility();
                _ = _host.StopAsync();
                MainPanel.Visibility = Visibility.Collapsed;
                ConnectPanel.Visibility = Visibility.Visible;
                ShowConnectError("Conexão com o servidor perdida.");
            });
            await conn.ConnectAsync(server, port);
            _conn = conn;

            AppState.ServerAddress = server;
            AppState.ServerPort = port;
            AppState.Save();

            RegisterHostNotifications();
            ConnectPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
            TxtServerLabel.Text = $"{server}:{port}";
            TxtHostId.Text = _hostActive && !string.IsNullOrEmpty(AppState.HostId) ? AppState.HostId : "—";
        }
        catch (Exception ex)
        {
            ShowConnectError("Falha ao conectar: " + ex.Message);
        }
        finally
        {
            SetConnectBusy(false);
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await StopHostAsync();
        if (_conn is not null) await _conn.DisposeAsync();
        _conn = null;
        MainPanel.Visibility = Visibility.Collapsed;
        ConnectPanel.Visibility = Visibility.Visible;
        ShowConnectError(null);
    }

    private void RegisterHostNotifications()
    {
        if (_conn is null) return;
        _conn.On(MsgTypes.ConnectNotify, env =>
        {
            var notify = EnvelopeUtil.Data<ConnectNotify>(env);
            if (notify is null) return;
            _host.AddPendingToken(notify.SessionToken);
            Dispatcher.Invoke(() => TxtHostStatus.Text = $"Conexão solicitada por {notify.ClientName} ({notify.ClientOs})");

            if (notify.RequiresApproval)
            {
                // Modo assistido: o usuário do host decide explicitamente.
                Dispatcher.Invoke(() =>
                {
                    var result = MessageBox.Show(this,
                        $"Conexão solicitada por {notify.ClientName} ({notify.ClientOs}).\nPermitir acesso a este computador?",
                        "RemoteEternal - Solicitação de acesso",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    _ = SendConnectAckSafeAsync(result == MessageBoxResult.Yes);
                });
            }
            else
            {
                // Modo não assistido: senha já validada pelo servidor; aceite automático.
                _ = SendConnectAckSafeAsync(true);
            }
        });
    }

    private async Task SendConnectAckSafeAsync(bool accepted)
    {
        try
        {
            if (_conn is null || string.IsNullOrEmpty(AppState.HostId)) return;
            await _conn.SendConnectAckAsync(AppState.HostId, accepted, accepted ? AppState.ListenPort : 0);
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => TxtHostStatus.Text = "Falha ao responder: " + ex.Message);
        }
    }

    private async void OnToggleHostClick(object sender, RoutedEventArgs e)
    {
        if (_conn is null || !_conn.IsConnected)
        {
            MessageBox.Show(this, "Conecte ao servidor primeiro", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_hostActive)
        {
            await StopHostAsync();
            return;
        }
        try
        {
            BtnToggleHost.IsEnabled = false;
            string mode = RadUnassisted.IsChecked == true ? HostAccess.Unassisted : HostAccess.Assisted;
            string? saltB64 = null;
            string? verifierB64 = null;
            if (mode == HostAccess.Unassisted)
            {
                string pass = TxtHostPass.Password;
                if (string.IsNullOrEmpty(pass))
                {
                    MessageBox.Show(this, "Defina uma senha para o modo não assistido", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                byte[] salt = PasswordHasher.GenerateSalt();
                byte[] verifier = PasswordHasher.Compute(salt, pass);
                saltB64 = Convert.ToBase64String(salt);
                verifierB64 = Convert.ToBase64String(verifier);
            }

            string hostId = await EnsureHostIdAsync();
            var online = await _conn.HostOnlineAsync(hostId, AppState.DeviceName, AppState.Os, AppState.ListenPort, mode, saltB64, verifierB64);
            if (!online.Ok)
            {
                // HostId persistido pode não existir mais no servidor (banco recriado);
                // registra um novo ID e tenta publicar novamente.
                if (online.Error?.Contains("ID não encontrado") == true)
                {
                    var reg = await _conn.RegisterHostAsync(AppState.DeviceName, AppState.Os);
                    if (!reg.Ok || string.IsNullOrEmpty(reg.HostId))
                    {
                        MessageBox.Show(this, reg.Error ?? "Falha ao registrar host", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    AppState.SaveHostId(reg.HostId!);
                    hostId = reg.HostId!;
                    online = await _conn.HostOnlineAsync(hostId, AppState.DeviceName, AppState.Os, AppState.ListenPort, mode, saltB64, verifierB64);
                }
                if (!online.Ok)
                {
                    MessageBox.Show(this, online.Error ?? "Falha ao publicar host", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            await _host.StartAsync(AppState.ListenPort);
            _hostActive = true;
            BtnToggleHost.Content = "Parar acesso";
            TxtHostId.Text = hostId;
            TxtHostStatus.Text = "Aguardando conexões.";
            UpdateHostPasswordVisibility();
        }
        catch (Exception ex)
        {
            await _host.StopAsync();
            MessageBox.Show(this, ex.Message, "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnToggleHost.IsEnabled = true;
        }
    }

    private async Task<string> EnsureHostIdAsync()
    {
        if (!string.IsNullOrEmpty(AppState.HostId)) return AppState.HostId;
        var reg = await _conn!.RegisterHostAsync(AppState.DeviceName, AppState.Os);
        if (!reg.Ok || string.IsNullOrEmpty(reg.HostId))
            throw new InvalidOperationException(reg.Error ?? "Falha ao registrar host");
        AppState.SaveHostId(reg.HostId!);
        return reg.HostId!;
    }

    private async Task StopHostAsync()
    {
        await _host.StopAsync();
        _hostActive = false;
        BtnToggleHost.Content = "Iniciar acesso";
        TxtHostId.Text = "—";
        TxtHostStatus.Text = "Acesso interrompido.";
        UpdateHostPasswordVisibility();
    }

    private async void OnConnectRemoteClick(object sender, RoutedEventArgs e)
    {
        if (_conn is null || !_conn.IsConnected)
        {
            MessageBox.Show(this, "Conecte ao servidor primeiro", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string hostId = TxtRemoteId.Text.Trim();
        if (!IsValidHostId(hostId))
        {
            MessageBox.Show(this, "Informe um ID de 6 dígitos", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            BtnConnectRemote.IsEnabled = false;
            TxtRemoteStatus.Text = "Consultando host...";

            var saltResult = await _conn.GetHostSaltAsync(hostId);
            if (!saltResult.Ok)
            {
                MessageBox.Show(this, saltResult.Error ?? "Host não encontrado", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? authHash = null;
            if (string.Equals(saltResult.AccessMode, HostAccess.Unassisted, StringComparison.Ordinal))
            {
                string pass = TxtRemotePass.Password;
                if (string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(saltResult.Salt))
                {
                    MessageBox.Show(this, "Este host exige senha", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                authHash = PasswordHasher.ComputeBase64(saltResult.Salt, pass);
            }

            TxtRemoteStatus.Text = "Aguardando aprovação / conexão...";
            var lookup = await _conn.LookupAsync(hostId, authHash);
            if (!lookup.Ok || string.IsNullOrEmpty(lookup.Ip) || lookup.Port <= 0 || string.IsNullOrEmpty(lookup.SessionToken))
            {
                MessageBox.Show(this, lookup.Error ?? "Falha na conexão", "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var viewer = new ViewerWindow(lookup.Ip!, lookup.Port, lookup.SessionToken!, "ID " + hostId);
            viewer.Owner = this;
            viewer.Show();
            TxtRemoteStatus.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "RemoteEternal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnConnectRemote.IsEnabled = true;
        }
    }

    private static bool IsValidHostId(string id) =>
        id.Length == 6 && id.All(char.IsAsciiDigit);

    private void OnAccessModeChanged(object sender, RoutedEventArgs e) => UpdateHostPasswordVisibility();

    private void UpdateHostPasswordVisibility()
    {
        bool unassisted = RadUnassisted.IsChecked == true;
        TxtHostPassLabel.Visibility = unassisted ? Visibility.Visible : Visibility.Collapsed;
        TxtHostPass.Visibility = unassisted ? Visibility.Visible : Visibility.Collapsed;
        TxtHostPass.IsEnabled = !_hostActive;
    }

    private void SetConnectBusy(bool busy)
    {
        BtnConnectServer.IsEnabled = !busy;
        if (busy) ShowConnectError(null);
    }

    private void ShowConnectError(string? message)
    {
        TxtConnectError.Text = message ?? "";
        TxtConnectError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _host.DisposeAsync();
        if (_conn is not null) await _conn.DisposeAsync();
        base.OnClosed(e);
    }
}