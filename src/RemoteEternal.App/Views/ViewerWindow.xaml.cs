using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RemoteEternal.App.Media;
using RemoteEternal.App.Services;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.App.Views;

public partial class ViewerWindow : Window
{
    private readonly SessionClient _client = new();
    private H264StreamDecoder? _decoder;
    private readonly AudioPlayer _audio = new();
    private WriteableBitmap? _bitmap;
    private int _frameWidth, _frameHeight;
    private readonly HashSet<ushort> _heldKeys = new();
    private bool _closing;
    private bool _fullscreen;
    private int _fpsCounter;
    private readonly System.Windows.Threading.DispatcherTimer _statsTimer;
    private SessionHello? _hello;
    private string _currentDisplay = "";
    private bool _suppressMonitorEvent;

    private const int Fps = 30;
    private const int BitrateKbps = 6000;
    private const int Quality = 60;

    public ViewerWindow(string ip, int port, string token, string deviceName)
    {
        InitializeComponent();
        _statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) =>
        {
            TxtStats.Text = $"{_fpsCounter} fps · {_frameWidth}x{_frameHeight}";
            _fpsCounter = 0;
        };
        _client.Connected += () => Dispatcher.InvokeAsync(() => TxtStatus.Text = "Aguardando informações da tela...");
        _client.MediaRestarted += () => Dispatcher.InvokeAsync(OnMediaRestart);
        _client.MediaFrameReceived += OnMediaFrame;
        _client.AudioFrameReceived += OnAudioFrame;
        _client.ErrorReceived += msg => Dispatcher.InvokeAsync(() => ShowError(msg));
        _client.Ended += reason => Dispatcher.InvokeAsync(() => CloseSession(reason));
        _client.Closed += () => Dispatcher.InvokeAsync(() =>
        {
            // Durante o handshake o motivo de falha é exibido pelo fluxo de ConnectAsync;
            // somente após o hello a perda de conexão deve encerrar a janela.
            if (_closing || _hello is null) return;
            CloseSession("Conexão perdida");
        });
        DiagnosticLog.LineWritten += OnDiagnosticLine;
        Loaded += async (_, _) =>
        {
            TxtTitle.Text = deviceName;
            Title = $"RemoteEternal - {deviceName}";
            _statsTimer.Start();
            TxtStatus.Text = "Conectando ao host...";
            try
            {
                SessionHello hello = await _client.ConnectAsync(ip, port, token);
                if (_closing) return;
                OnHello(hello);
            }
            catch (Exception ex)
            {
                if (_closing) return;
                ShowError(FormatConnectError(ex));
            }
        };
    }

    private void OnDiagnosticLine(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtDiagnostics.AppendText(line + Environment.NewLine);
            TrimDiagnostics();
        });
    }

    private void TrimDiagnostics()
    {
        const int maxLines = 500;
        while (TxtDiagnostics.LineCount > maxLines)
        {
            int idx = TxtDiagnostics.Text.IndexOf('\n');
            if (idx < 0) break;
            TxtDiagnostics.Text = TxtDiagnostics.Text.Substring(idx + 1);
        }
        TxtDiagnostics.ScrollToEnd();
    }

    private void OnHello(SessionHello hello)
    {
        if (hello is null || hello.Displays is null || hello.Displays.Length == 0)
        {
            ShowError("O host não enviou informações de tela válidas.");
            return;
        }
        _hello = hello;
        _suppressMonitorEvent = true;
        CboMonitor.Items.Clear();
        foreach (var d in hello.Displays)
            CboMonitor.Items.Add($"{d.Name} ({d.Width}x{d.Height})");
        int defaultIndex = Math.Clamp(hello.DefaultDisplayIndex, 0, hello.Displays.Length - 1);
        CboMonitor.SelectedIndex = defaultIndex;
        _suppressMonitorEvent = false;
        _currentDisplay = hello.Displays[defaultIndex].Id;
        // O decoder NÃO é aberto aqui. O host só inicia o envio de mídia depois de receber o
        // start e sinaliza isso com SessionMediaRestart; o cliente então limpa o MediaBuffer
        // (Media.Clear() em SessionClient.HandleControl) e dispara OnMediaRestart, que chama
        // StartDecoder() sobre o buffer já limpo — garantindo que o FfmpegDecoder lê o stream
        // MP4 fragmentado desde o início (ftyp/moov). Abrir o decoder aqui criaria um primeiro
        // decoder que bloqueia e consome o início do stream; quando o mediaRestart chegar, o
        // StartDecoder() seguinte leria dados do meio do stream já consumidos, causando
        // "Invalid data found when processing input".
        _ = _client.SendStartAsync(_currentDisplay, Fps, BitrateKbps, Quality, true);
    }

    private void OnMediaRestart()
    {
        TxtStatus.Visibility = Visibility.Visible;
        TxtStatus.Text = "Reiniciando vídeo...";
        StartDecoder();
    }

    /// <summary>Recebe um frame H.264 cru do host ([flags(1)][ptsMs(8)][nalData]) e alimenta o decoder.</summary>
    private void OnMediaFrame(byte[] payload)
    {
        if (payload.Length < 9) return;
        bool isKey = (payload[0] & 1) != 0;
        long pts = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1));
        var nal = new byte[payload.Length - 9];
        Buffer.BlockCopy(payload, 9, nal, 0, nal.Length);
        _decoder?.FeedPacket(nal, isKey, pts);
    }

    /// <summary>Recebe um frame de áudio PCM ([sampleRate(4)][channels(1)][pcm16le]) e o reproduz.</summary>
    private void OnAudioFrame(byte[] payload)
    {
        if (payload.Length < 6) return;
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0));
        int channels = payload[4];
        if (channels <= 0) return;
        try
        {
            _audio.SetFormat(sampleRate, channels);
            _audio.AddSamples(payload, 5, payload.Length - 5);
        }
        catch { }
    }

    private void StartDecoder()
    {
        var old = _decoder;
        _decoder = null;
        old?.Dispose();
        _audio.Restart();
        try
        {
            var decoder = new H264StreamDecoder();
            decoder.VideoFrameReady += OnVideoFrame;
            _decoder = decoder;
            TxtStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // A construção (ex.: DllNotFoundException das DLLs FFmpeg ausentes) e o Start
            // ficam sob o mesmo guarda; descarta o decoder parcialmente construído para
            // liberar recursos nativos e mostra uma mensagem acionável em TxtStatus.
            var failed = _decoder;
            _decoder = null;
            failed?.Dispose();
            ShowError("Falha ao iniciar vídeo: " + ex.Message + ". Verifique as DLLs FFmpeg.");
        }
    }

    private void OnVideoFrame(byte[] bgra, int width, int height)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    RemoteImage.Source = _bitmap;
                    RemoteImage.Visibility = Visibility.Visible;
                    TxtStatus.Visibility = Visibility.Collapsed;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
                _frameWidth = width;
                _frameHeight = height;
                _fpsCounter++;
            }
            catch
            {
            }
        });
    }

    private void OnMonitorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressMonitorEvent || _hello is null || CboMonitor.SelectedIndex < 0) return;
        if (CboMonitor.SelectedIndex >= _hello.Displays.Length) return;
        string id = _hello.Displays[CboMonitor.SelectedIndex].Id;
        if (id == _currentDisplay) return;
        _currentDisplay = id;
        TxtStatus.Visibility = Visibility.Visible;
        TxtStatus.Text = "Trocando monitor...";
        _ = _client.SendSwitchDisplayAsync(id);
    }

    private void OnAudioChanged(object sender, RoutedEventArgs e)
    {
        if (_currentDisplay.Length == 0) return;
        bool enabled = ChkAudio.IsChecked == true;
        _ = _client.SendStartAsync(_currentDisplay, Fps, BitrateKbps, Quality, enabled);
        if (!enabled) _audio.Restart();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Topmost = false;
        }
        BtnFullscreen.Content = _fullscreen ? "Sair da tela cheia" : "Tela cheia";
        RemoteImage.Focus();
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        CloseSession("Desconectado pelo usuário");
    }

    private void CloseSession(string reason)
    {
        if (_closing) return;
        _closing = true;
        try { _ = _client.SendEndAsync(); } catch { }
        TxtTitle.Text = reason;
        Title = $"RemoteEternal - {reason}";
        _statsTimer.Stop();
        _audio.Dispose();
        var d = _decoder;
        _decoder = null;
        d?.Dispose();
        _ = _client.DisposeAsync();
        Close();
    }

    private (int x, int y)? MapToSource(Point p)
    {
        if (_frameWidth <= 0 || _frameHeight <= 0 || RemoteImage.Visibility != Visibility.Visible)
            return null;
        double iw = RemoteImage.ActualWidth;
        double ih = RemoteImage.ActualHeight;
        if (iw <= 0 || ih <= 0) return null;
        double scale = Math.Min(iw / _frameWidth, ih / _frameHeight);
        double dispW = _frameWidth * scale;
        double dispH = _frameHeight * scale;
        double offsetX = (iw - dispW) / 2;
        double offsetY = (ih - dispH) / 2;
        double x = (p.X - offsetX) / scale;
        double y = (p.Y - offsetY) / scale;
        if (x < 0 || y < 0 || x >= _frameWidth || y >= _frameHeight) return null;
        return ((int)x, (int)y);
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        var pos = MapToSource(e.GetPosition(RemoteImage));
        if (pos is null) return;
        _ = _client.SendInputAsync(InputEncoder.MouseMove(pos.Value.x, pos.Value.y));
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        RemoteImage.Focus();
        var button = MapButton(e.ChangedButton);
        if (button == 0) return;
        _ = _client.SendInputAsync(InputEncoder.MouseButton(true, button));
        OnImageMouseMove(sender, e);
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        var button = MapButton(e.ChangedButton);
        if (button == 0) return;
        _ = _client.SendInputAsync(InputEncoder.MouseButton(false, button));
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _ = _client.SendInputAsync(InputEncoder.MouseWheel(e.Delta));
    }

    private static byte MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Right => 2,
        MouseButton.Middle => 3,
        _ => 0
    };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_closing) return;
        if (e.Key == Key.Escape && _fullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk <= 0) return;
        ushort code = (ushort)vk;
        if (e.IsRepeat) { e.Handled = true; return; }
        _heldKeys.Add(code);
        _ = _client.SendInputAsync(InputEncoder.KeyEvent(true, code));
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_closing) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk <= 0) return;
        ushort code = (ushort)vk;
        _heldKeys.Remove(code);
        _ = _client.SendInputAsync(InputEncoder.KeyEvent(false, code));
        e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        foreach (var code in _heldKeys.ToArray())
        {
            _ = _client.SendInputAsync(InputEncoder.KeyEvent(false, code));
            _heldKeys.Remove(code);
        }
    }

    private void ShowError(string message)
    {
        TxtStatus.Visibility = Visibility.Visible;
        TxtStatus.Text = message;
    }

    private static string FormatConnectError(Exception ex) => ex switch
    {
        TimeoutException t when !string.IsNullOrEmpty(t.Message) => t.Message,
        IOException io when !string.IsNullOrEmpty(io.Message) => io.Message,
        _ => "Não foi possível estabelecer a sessão remota."
    };

    protected override void OnClosed(EventArgs e)
    {
        DiagnosticLog.LineWritten -= OnDiagnosticLine;
        _statsTimer.Stop();
        if (!_closing)
        {
            _closing = true;
            try { _ = _client.SendEndAsync(); } catch { }
            _audio.Dispose();
            _decoder?.Dispose();
            _ = _client.DisposeAsync();
        }
        base.OnClosed(e);
    }
}
