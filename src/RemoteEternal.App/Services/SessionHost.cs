using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using RemoteEternal.Core.Crypto;
using RemoteEternal.Core.Net;
using RemoteEternal.Core.Protocol;
using RemoteEternal.App.Input;
using ScreenRecorderLib;

namespace RemoteEternal.App.Services;

public class SessionHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DateTime> _pendingTokens = new();
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _activeLock = new(1, 1);
    private Recorder? _recorder;
    private SecureFrameChannel? _channel;
    private MonitorInfo? _activeMonitor;
    private List<MonitorInfo> _monitors = new();
    private SessionStream? _mediaStream;
    private Task? _acceptTask;

    public event Action<string>? StatusChanged;
    public event Action<bool>? SessionActiveChanged;

    public void AddPendingToken(string tokenB64)
    {
        _pendingTokens[tokenB64] = DateTime.UtcNow;
        CleanupExpiredTokens();
    }

    private void CleanupExpiredTokens()
    {
        var limit = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var kv in _pendingTokens.Where(kv => kv.Value < limit))
            _pendingTokens.TryRemove(kv.Key, out _);
    }

    public async Task StartAsync(int port)
    {
        if (_listener is not null || _acceptTask is not null)
            await StopAsync().ConfigureAwait(false);
        else
            _cts.Dispose();

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoopAsync);
        StatusChanged?.Invoke($"Ouvindo na porta {port}");
    }

    /// <summary>Para o listener, cancela sessões ativas e limpa tokens pendentes, mas mantém o objeto
    /// reutilizável (StartAsync pode ser chamado novamente).</summary>
    public async Task StopAsync()
    {
        var old = _cts;
        _cts = new CancellationTokenSource();
        old.Cancel();
        _pendingTokens.Clear();
        StopCapture();
        TcpListener? listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            try { listener.Stop(); } catch { }
        }
        try { await (_acceptTask ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        _acceptTask = null;
        old.Dispose();
        StatusChanged?.Invoke("Acesso interrompido");
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        var cts = _cts;
        while (!cts.IsCancellationRequested && listener is not null)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleConnectionAsync(tcp));
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp)
    {
        using (tcp)
        {
            var sessionCts = _cts;
            var stream = tcp.GetStream();
            try
            {
                byte[] token = new byte[32];
                await FrameChannel.ReadExactlyAsync(stream, token, 32, CancellationToken.None).ConfigureAwait(false);
                string key = Convert.ToBase64String(token);
                if (!_pendingTokens.TryRemove(key, out _))
                {
                    stream.WriteByte(0);
                    return;
                }
                stream.WriteByte(1);
                await stream.FlushAsync().ConfigureAwait(false);

                _channel = SecureFrameChannel.CreateDirectional(stream, token, System.Text.Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1), "re-session"u8.ToArray(), SessionRole.Host);

                if (!await _activeLock.WaitAsync(0).ConfigureAwait(false))
                {
                    await SendControlAsync(new SessionEnd("Outra sessão já está ativa")).ConfigureAwait(false);
                    return;
                }

                _monitors = MonitorEnumeration.GetMonitors();
                StatusChanged?.Invoke("Sessão ativa");
                SessionActiveChanged?.Invoke(true);
                try
                {
                    await SendHelloAsync().ConfigureAwait(false);
                    await ReceiveLoopAsync(sessionCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    StopCapture();
                    _channel = null;
                    _activeLock.Release();
                    StatusChanged?.Invoke("Sessão encerrada");
                    SessionActiveChanged?.Invoke(false);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Erro na sessão: {ex.Message}");
                try
                {
                    _activeLock.Release();
                }
                catch
                {
                }
                SessionActiveChanged?.Invoke(false);
            }
        }
    }

    private async Task SendHelloAsync()
    {
        var displays = Recorder.GetDisplays();
        var infos = new List<DisplayInfo>();
        int defaultIndex = 0;
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            var m = MonitorEnumeration.Find(d.DeviceName);
            int width = m?.Width ?? 1920;
            int height = m?.Height ?? 1080;
            int left = m?.Left ?? 0;
            int top = m?.Top ?? 0;
            if (m?.IsPrimary == true) defaultIndex = i;
            infos.Add(new DisplayInfo(d.DeviceName, d.FriendlyName, width, height, left, top));
        }
        await SendControlAsync(new SessionHello(AppState.DeviceName, infos.ToArray(), defaultIndex)).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (_channel is not null && !ct.IsCancellationRequested)
        {
            var (type, payload) = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            switch (type)
            {
                case SecureFrameChannel.TypeControl:
                    await HandleControlAsync(payload).ConfigureAwait(false);
                    break;
                case SecureFrameChannel.TypeInput:
                    HandleInput(payload);
                    break;
            }
        }
    }

    private async Task HandleControlAsync(byte[] payload)
    {
        var env = EnvelopeUtil.Parse(payload);
        switch (env.Type)
        {
            case SessionControlTypes.Start:
                var start = EnvelopeUtil.Data<SessionStart>(env);
                if (start is not null) await StartCaptureAsync(start.DisplayId, start.Fps, start.BitrateKbps, start.Quality, start.AudioEnabled).ConfigureAwait(false);
                break;
            case SessionControlTypes.SwitchDisplay:
                var sw = EnvelopeUtil.Data<SessionSwitchDisplay>(env);
                if (sw is not null) await StartCaptureAsync(sw.DisplayId, CurrentFps, CurrentBitrateKbps, CurrentQuality, CurrentAudioEnabled).ConfigureAwait(false);
                break;
            case SessionControlTypes.End:
                throw new IOException("Sessão encerrada pelo cliente");
        }
    }

    private int CurrentFps { get; set; } = 30;
    private int CurrentBitrateKbps { get; set; } = 6000;
    private int CurrentQuality { get; set; } = 60;
    private bool CurrentAudioEnabled { get; set; } = true;

    private async Task StartCaptureAsync(string displayId, int fps, int bitrateKbps, int quality, bool audioEnabled)
    {
        StopCapture();
        await Task.Yield();

        CurrentFps = fps;
        CurrentBitrateKbps = bitrateKbps;
        CurrentQuality = quality;
        CurrentAudioEnabled = audioEnabled;

        var display = Recorder.GetDisplays().FirstOrDefault(d => d.DeviceName == displayId);
        if (display is null)
        {
            await SendControlAsync(new SessionEnd("Monitor não encontrado")).ConfigureAwait(false);
            throw new IOException("Monitor não encontrado");
        }
        _activeMonitor = MonitorEnumeration.Find(displayId);

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase>
                {
                    new DisplayRecordingSource { DeviceName = display.DeviceName, IsCursorCaptureEnabled = true }
                }
            },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioEnabled,
                Channels = AudioChannels.Stereo,
                Bitrate = AudioBitrate.bitrate_128kbps
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = fps,
                Bitrate = bitrateKbps * 1000,
                Quality = quality,
                Encoder = new H264VideoEncoder
                {
                    EncoderProfile = H264Profile.High,
                    BitrateMode = H264BitrateControlMode.CBR
                },
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = true,
                IsFragmentedMp4Enabled = true,
                IsMp4FastStartEnabled = false,
                IsFixedFramerate = false,
                IsThrottlingDisabled = true
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
            LogOptions = new LogOptions { IsLogEnabled = false }
        };

        _mediaStream = new SessionStream(_channel!);
        var recorder = Recorder.CreateRecorder(options);
        recorder.OnRecordingFailed += (_, e) => StatusChanged?.Invoke($"Falha na captura: {e.Error}");
        _recorder = recorder;

        await SendControlAsync(new SessionMediaRestart("Nova captura"), SessionControlTypes.MediaRestart).ConfigureAwait(false);
        recorder.Record(_mediaStream);
        StatusChanged?.Invoke($"Capturando: {display.FriendlyName}");
    }

    private void StopCapture()
    {
        try
        {
            _recorder?.Stop();
            _recorder?.Dispose();
        }
        catch
        {
        }
        _recorder = null;
        _mediaStream?.Stop();
        _mediaStream = null;
    }

    private void HandleInput(byte[] payload)
    {
        if (payload.Length == 0) return;
        switch (payload[0])
        {
            case InputEvents.MouseMove when payload.Length >= 9:
                int x = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1));
                int y = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5));
                var m = _activeMonitor;
                int vx = (m?.Left ?? 0) + x;
                int vy = (m?.Top ?? 0) + y;
                InputSimulator.MoveMouseAbsolute(vx, vy);
                break;
            case InputEvents.MouseDown when payload.Length >= 2:
                InputSimulator.MouseButton(payload[1], true);
                break;
            case InputEvents.MouseUp when payload.Length >= 2:
                InputSimulator.MouseButton(payload[1], false);
                break;
            case InputEvents.MouseWheel when payload.Length >= 5:
                InputSimulator.MouseWheel(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1)));
                break;
            case InputEvents.KeyDown when payload.Length >= 3:
                InputSimulator.KeyEvent(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1)), true);
                break;
            case InputEvents.KeyUp when payload.Length >= 3:
                InputSimulator.KeyEvent(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1)), false);
                break;
        }
    }

    private Task SendControlAsync(object data, string? typeOverride = null)
    {
        string type = typeOverride ?? data switch
        {
            SessionHello => SessionControlTypes.Hello,
            SessionEnd => SessionControlTypes.End,
            SessionMediaRestart => SessionControlTypes.MediaRestart,
            _ => SessionControlTypes.Error
        };
        byte[] json = EnvelopeUtil.Create(type, data);
        return _channel!.SendAsync(SecureFrameChannel.TypeControl, json);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
