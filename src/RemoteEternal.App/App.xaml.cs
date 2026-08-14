using System.Windows;
using System.Windows.Threading;
using RemoteEternal.App.Services;
using RemoteEternal.App.Views;

namespace RemoteEternal.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Registra o log global ANTES de qualquer inicialização de janela:
        // qualquer exceção no construtor de MainWindow, no AppState ou na
        // extração FFmpeg é capturada e gravada em %APPDATA%\RemoteEternal\error.log.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Falha na inicialização do RemoteEternal (MainWindow)");
            MessageBox.Show(
                "O RemoteEternal não conseguiu iniciar.\n\nDetalhes do erro foram gravados em:\n" + ErrorLog.LogPath,
                "RemoteEternal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ErrorLog.Write(ex, "Exceção não tratada (AppDomain)");
        else
            ErrorLog.Write("Exceção não tratada (AppDomain) com objeto desconhecido: " + e.ExceptionObject);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "Exceção não tratada (Dispatcher)");
        // Handled = false: deixa o processo terminar como o WPF faria, mas o log
        // já foi gravado para diagnóstico. Não mascara o crash silencioso.
        e.Handled = false;
    }
}
