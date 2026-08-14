using System.Collections.Generic;
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
    private FfmpegDecoder? _decoder;
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
        _client.HelloReceived += OnHello;
        _client.MediaRestarted += OnMediaRestart;
        _client.ErrorReceived += msg => Dispatcher.Invoke(() => ShowError(msg));
        _client.Ended += reason => Dispatcher.Invoke(() => CloseSession(reason));
        _client.Closed += () => Dispatcher.Invoke(() => CloseSession("Conexão perdida"));
        Loaded += async (_, _) =>
        {
            TxtTitle.Text = deviceName;
            Title = $"RemoteEternal - {deviceName}";
            _statsTimer.Start();
            try
            {
                await _client.ConnectAsync(ip, port, token);
            }
            catch (Exception ex)
            {
                ShowError("Falha ao conectar: " + ex.Message);
            }
        };
    }

    private void OnHello(SessionHello hello)
    {
        Dispatcher.Invoke(() =>
        {
            _hello = hello;
            _suppressMonitorEvent = true;
            CboMonitor.Items.Clear();
            foreach (var d in hello.Displays)
                CboMonitor.Items.Add($"{d.Name} ({d.Width}x{d.Height})");
            CboMonitor.SelectedIndex = Math.Clamp(hello.DefaultDisplayIndex, 0, Math.Max(0, hello.Displays.Length - 1));
            _suppressMonitorEvent = false;
            _currentDisplay = hello.Displays[CboMonitor.SelectedIndex].Id;
            StartDecoder();
            _ = _client.SendStartAsync(_currentDisplay, Fps, BitrateKbps, Quality, true);
        });
    }

    private void OnMediaRestart()
    {
        Dispatcher.Invoke(() =>
        {
            TxtStatus.Visibility = Visibility.Visible;
            TxtStatus.Text = "Reiniciando vídeo...";
            StartDecoder();
        });
    }

    private void StartDecoder()
    {
        var old = _decoder;
        _decoder = null;
        old?.Dispose();
        _audio.Restart();
        try
        {
            var decoder = new FfmpegDecoder(_client.Media);
            decoder.VideoFrameReady += OnVideoFrame;
            decoder.AudioReady += (pcm, rate, ch) =>
            {
                try
                {
                    _audio.SetFormat(rate, ch);
                    _audio.AddSamples(pcm, 0, pcm.Length);
                }
                catch
                {
                }
            };
            _decoder = decoder;
            decoder.Start();
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

    protected override void OnClosed(EventArgs e)
    {
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
