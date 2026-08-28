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

    /// <summary>Tempo máximo para o primeiro envio do hello (informações das telas). Curto por
    /// design: se o write/flush não concluir, a falha deve aparecer no host e no cliente sem
    /// deixar a sessão "presa" no handshake.</summary>
    private static readonly TimeSpan HelloSendTimeout = TimeSpan.FromSeconds(6);

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
        DiagnosticLog.Write("SessionHost", $"Listener iniciado (porta {port})");
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
            DiagnosticLog.Write("SessionHost", "Conexão TCP aceita");
            bool activeLockHeld = false;
            SecureFrameChannel? channel = null;
            try
            {
                byte[] token = new byte[32];
                await FrameChannel.ReadExactlyAsync(stream, token, 32, CancellationToken.None).ConfigureAwait(false);
                string key = Convert.ToBase64String(token);
                if (!_pendingTokens.TryRemove(key, out _))
                {
                    DiagnosticLog.Write("SessionHost", "Token rejeitado (ACK=0)");
                    stream.WriteByte(0);
                    return;
                }
                stream.WriteByte(1);
                await stream.FlushAsync().ConfigureAwait(false);
                DiagnosticLog.Write("SessionHost", "Token validado (ACK=1 enviado)");

                channel = SecureFrameChannel.CreateDirectional(stream, token, System.Text.Encoding.UTF8.GetBytes(SecureFrameChannel.SessionSaltV1), "re-session"u8.ToArray(), SessionRole.Host);
                DiagnosticLog.Write("SessionHost", "Canal seguro criado (role=Host)");

                if (!await _activeLock.WaitAsync(0).ConfigureAwait(false))
                {
                    DiagnosticLog.Write("SessionHost", "Lock ocupado — rejeitando segunda sessão");
                    // Rejeita a segunda conexão SEM tocar no _channel da sessão ativa; o canal
                    // rejeitado é apenas local e não deve ficar referenciado pelo campo.
                    using var rejectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                    rejectCts.CancelAfter(HelloSendTimeout);
                    try
                    {
                        await channel.SendAsync(
                            SecureFrameChannel.TypeControl,
                            EnvelopeUtil.Create(SessionControlTypes.End, new SessionEnd("Outra sessão já está ativa")),
                            rejectCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A rejeição é apenas informativa; a sessão ativa não é afetada.
                    }
                    return;
                }
                activeLockHeld = true;
                DiagnosticLog.Write("SessionHost", "Lock de sessão adquirido");
                _channel = channel;

                _monitors = MonitorEnumeration.GetMonitors();
                DiagnosticLog.Write("SessionHost", $"Monitores enumerados: {_monitors.Count}");
                StatusChanged?.Invoke("Sessão ativa");
                SessionActiveChanged?.Invoke(true);
                await SendHelloAsync().ConfigureAwait(false);
                await ReceiveLoopAsync(sessionCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string message = SanitizeSessionError(ex);
                DiagnosticLog.Write("SessionHost", $"HandleConnection {ex.GetType().Name}: {message}");
                StatusChanged?.Invoke(message);
                ErrorLog.Write($"SessionHost.HandleConnection {ex.GetType().Name}: {message}");
            }
            finally
            {
                // Libera exatamente uma vez (flag local) e nunca deixa o _channel apontando
                // para a conexão já encerrada (ReferenceEquals protege uma sessão mais nova).
                if (ReferenceEquals(_channel, channel)) _channel = null;
                StopCapture();
                if (activeLockHeld)
                {
                    activeLockHeld = false;
                    _activeLock.Release();
                    StatusChanged?.Invoke("Sessão encerrada");
                    SessionActiveChanged?.Invoke(false);
                }
            }
        }
    }

    /// <summary>Mensagem de status segura: nunca expõe IP, token, payload ou nomes de monitor.
    /// <see cref="TimeoutException"/> e <see cref="IOException"/> lançadas pelo App já têm texto
    /// seguro; demais exceções viram mensagem genérica acionável.</summary>
    private static string SanitizeSessionError(Exception ex) => ex switch
    {
        TimeoutException t when !string.IsNullOrEmpty(t.Message) => t.Message,
        IOException io when !string.IsNullOrEmpty(io.Message) => io.Message,
        _ => "A sessão remota foi encerrada por uma falha inesperada."
    };

    private async Task SendHelloAsync()
    {
        if (_monitors.Count == 0)
        {
            StatusChanged?.Invoke("Falha: nenhum monitor disponível para iniciar a sessão");
            await SendControlAsync(new SessionEnd("Nenhum monitor disponível no host")).ConfigureAwait(false);
            throw new IOException("Nenhum monitor disponível");
        }

        DiagnosticLog.Write("SessionHost", $"Montando hello com {_monitors.Count} monitores");
        var infos = new List<DisplayInfo>(_monitors.Count);
        int defaultIndex = 0;
        for (int i = 0; i < _monitors.Count; i++)
        {
            var monitor = _monitors[i];
            if (monitor.IsPrimary) defaultIndex = i;
            infos.Add(new DisplayInfo(
                monitor.DeviceName,
                MonitorEnumeration.FriendlyName(monitor.DeviceName),
                monitor.Width,
                monitor.Height,
                monitor.Left,
                monitor.Top));
        }

        var hello = new SessionHello(AppState.DeviceName, infos.ToArray(), defaultIndex);

        // Serializa o envelope explicitamente e valida a criação ANTES de qualquer write:
        // um JSON inválido é detectado localmente, sem enviar bytes parciais ao cliente.
        byte[] envelope;
        try
        {
            envelope = EnvelopeUtil.Create(SessionControlTypes.Hello, hello);
            if (envelope.Length == 0)
                throw new InvalidOperationException("Envelope do hello vazio");
            _ = EnvelopeUtil.Parse(envelope); // round-trip local confirma que o JSON é legível
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Falha ao montar as informações da tela para envio.");
            ErrorLog.Write($"SessionHost.SendHelloAsync {ex.GetType().Name}: falha ao serializar o envelope do hello");
            throw new IOException("Não foi possível montar as informações da tela para envio.");
        }

        DiagnosticLog.Write("SessionHost", $"Envelope hello serializado ({envelope.Length} bytes)");
        StatusChanged?.Invoke($"Enviando monitores: {infos.Count}");

        // Envio com timeout curto dedicado; o token chega até SecureFrameChannel.SendAsync.
        var sessionCts = _cts;
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
        sendCts.CancelAfter(HelloSendTimeout);
        try
        {
            DiagnosticLog.Write("SessionHost", "Enviando hello.");
            await SendControlAsync(hello, ct: sendCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!sessionCts.IsCancellationRequested)
        {
            DiagnosticLog.Write("SessionHost", $"SendHelloAsync: tempo esgotado ao enviar as informações de {infos.Count} monitores ao cliente");
            StatusChanged?.Invoke($"Tempo esgotado ao enviar as informações de {infos.Count} monitores ao cliente. A conexão será encerrada.");
            throw new TimeoutException($"Tempo esgotado ao enviar as informações de {infos.Count} monitores ao cliente.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            DiagnosticLog.Write("SessionHost", $"SendHelloAsync {ex.GetType().Name}: falha no envio do hello");
            StatusChanged?.Invoke("Falha ao enviar as informações da tela ao cliente. A conexão será encerrada.");
            ErrorLog.Write($"SessionHost.SendHelloAsync {ex.GetType().Name}: falha no envio do hello");
            throw new IOException("Não foi possível enviar as informações da tela ao cliente.");
        }

        // O write/flush concluiu de fato: o status é inequívoco.
        DiagnosticLog.Write("SessionHost", "Hello enviado/flush concluído");
        StatusChanged?.Invoke($"Informações de {infos.Count} monitores enviadas; aguardando início da captura.");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        DiagnosticLog.Write("SessionHost", "Aguardando frames do cliente.");
        while (_channel is not null && !ct.IsCancellationRequested)
        {
            var (type, payload) = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            DiagnosticLog.Write("SessionHost", $"Frame recebido type={type} bytes={payload.Length}");
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
        DiagnosticLog.Write("SessionHost", $"Controle recebido: {env.Type}");
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
                DiagnosticLog.Write("SessionHost", "End recebido do cliente — encerrando");
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

        DiagnosticLog.Write("SessionHost", $"StartCapture: monitor={MonitorEnumeration.FriendlyName(displayId)} fps={fps} bitrate={bitrateKbps} quality={quality} audio={audioEnabled}");
        CurrentFps = fps;
        CurrentBitrateKbps = bitrateKbps;
        CurrentQuality = quality;
        CurrentAudioEnabled = audioEnabled;

        var monitor = _monitors.FirstOrDefault(m =>
            MonitorEnumeration.SameDisplay(m.DeviceName, displayId));
        if (monitor is null)
        {
            StatusChanged?.Invoke($"Falha: monitor solicitado não está no snapshot ({displayId})");
            await SendControlAsync(new SessionEnd("Monitor não encontrado no host")).ConfigureAwait(false);
            throw new IOException("Monitor não encontrado no snapshot do host");
        }

        // ScreenRecorderLib is queried only after the hello/start handshake. Its
        // native enumeration can block, so it must never be part of SendHelloAsync.
        StatusChanged?.Invoke($"Resolvendo monitor para captura: {monitor.DeviceName}");
        dynamic? display = null;
        try
        {
            var recorderDisplays = Recorder.GetDisplays();
            display = recorderDisplays.FirstOrDefault(d =>
                MonitorEnumeration.SameDisplay(d.DeviceName, monitor.DeviceName));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Falha ao enumerar monitores para captura: {ex.Message}");
            await SendControlAsync(new SessionEnd("Falha ao enumerar o monitor para captura")).ConfigureAwait(false);
            throw;
        }

        if (display is null)
        {
            StatusChanged?.Invoke($"Falha: monitor não encontrado no ScreenRecorderLib ({monitor.DeviceName})");
            await SendControlAsync(new SessionEnd("Monitor não encontrado pelo ScreenRecorderLib")).ConfigureAwait(false);
            throw new IOException("Monitor não encontrado pelo ScreenRecorderLib");
        }
        _activeMonitor = monitor;
        DiagnosticLog.Write("SessionHost", $"StartCapture: display encontrado: {display.FriendlyName}");

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
        DiagnosticLog.Write("SessionHost", "StartCapture: recorder criado");
        recorder.OnRecordingFailed += (_, e) =>
        {
            DiagnosticLog.Write("SessionCapture", $"OnRecordingFailed: {e.Error}");
            StatusChanged?.Invoke($"Falha na captura: {e.Error}");
        };
        recorder.OnRecordingComplete += (_, e) =>
            DiagnosticLog.Write("SessionCapture", $"OnRecordingComplete: {e.FilePath}");
        recorder.OnStatusChanged += (_, e) =>
            DiagnosticLog.Write("SessionCapture", $"OnStatusChanged: {e.Status}");
        _recorder = recorder;

        await SendControlAsync(new SessionMediaRestart("Nova captura"), SessionControlTypes.MediaRestart).ConfigureAwait(false);
        DiagnosticLog.Write("SessionHost", "StartCapture: chamando Record");
        recorder.Record(_mediaStream);
        DiagnosticLog.Write("SessionHost", "StartCapture: Record retornou");
        StatusChanged?.Invoke($"Capturando: {display.FriendlyName}");
        DiagnosticLog.Write("SessionHost", $"StartCapture: capturando {display.FriendlyName}");
    }

    private void StopCapture()
    {
        DiagnosticLog.Write("SessionHost", "StopCapture chamado");
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

    private Task SendControlAsync(object data, string? typeOverride = null, CancellationToken ct = default)
    {
        string type = typeOverride ?? data switch
        {
            SessionHello => SessionControlTypes.Hello,
            SessionEnd => SessionControlTypes.End,
            SessionMediaRestart => SessionControlTypes.MediaRestart,
            _ => SessionControlTypes.Error
        };
        byte[] json = EnvelopeUtil.Create(type, data);
        return _channel!.SendAsync(SecureFrameChannel.TypeControl, json, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
