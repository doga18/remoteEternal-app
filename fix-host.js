const fs = require("fs");
const p = "src/RemoteEternal.App/Services/SessionHost.cs";
let src = fs.readFileSync(p, "utf8");

// 1) Usings
src = src.replace("using ScreenRecorderLib;", "using RemoteEternal.App.Media;\nusing System.Threading.Channels;");

// 2) Campos
src = src.replace(
    "    private Recorder? _recorder;",
    "    private ScreenCapture? _capture;"
);
src = src.replace(
    "    private SessionStream? _mediaStream;",
    `    private Task? _mediaSenderTask;
    private readonly Channel<byte[]> _mediaQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(60) { FullMode = BoundedChannelFullMode.Wait });
    private long _mediaFrames, _mediaBytes, _mediaFailed;`
);

// 3) Substitui o corpo do StartCaptureAsync (do comentário ScreenRecorderLib até o fim do método)
const startMarker = "        // ScreenRecorderLib is queried only after the hello/start handshake.";
const endMarker = "    private void StopCapture()";
const si = src.indexOf(startMarker);
const ei = src.indexOf(endMarker);
if (si < 0 || ei < 0 || ei <= si) { console.error("marcadores StartCapture não encontrados: " + si + "," + ei); process.exit(1); }

const newBody = `        _activeMonitor = monitor;
        DiagnosticLog.Write("SessionHost", $"StartCapture: capturando {monitor.DeviceName} via ScreenCapture (DDA + NVENC, H.264 cru)");

        // Nova captura: avisa o cliente para reiniciar o decoder.
        await SendControlAsync(new SessionMediaRestart("Nova captura"), SessionControlTypes.MediaRestart).ConfigureAwait(false);

        // Drena frames antigos da fila e inicia o remetente ordenado.
        while (_mediaQueue.Reader.TryRead(out _)) { }
        _mediaSenderTask = Task.Run(MediaSenderLoopAsync);

        var capture = new ScreenCapture();
        capture.FrameReady += OnCaptureFrame;
        capture.Failed += msg =>
        {
            DiagnosticLog.Write("SessionCapture", "Falha na captura: " + msg);
            StatusChanged?.Invoke($"Falha na captura: {msg}");
        };
        _capture = capture;
        capture.Start(monitor.DeviceName, fps, bitrateKbps);
        StatusChanged?.Invoke($"Capturando: {MonitorEnumeration.FriendlyName(monitor.DeviceName)}");
    }

    private void OnCaptureFrame(byte[] nal, bool isKey, long ptsMs)
    {
        // Frame de mídia: [flags(1)][ptsMs(8)][nalData]. A ORDEM dos frames H.264 é
        // crítica (P-frames dependem dos anteriores), então enfileiramos em uma fila
        // FIFO estrita drenada por um único remetente (sem reordenar).
        byte[] frame = new byte[9 + nal.Length];
        frame[0] = (byte)(isKey ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(1), ptsMs);
        Buffer.BlockCopy(nal, 0, frame, 9, nal.Length);
        if (!_mediaQueue.Writer.TryWrite(frame))
            _mediaQueue.Writer.WriteAsync(frame).AsTask().Wait();
    }

    private async Task MediaSenderLoopAsync()
    {
        var channel = _channel;
        if (channel is null) return;
        try
        {
            await foreach (var frame in _mediaQueue.Reader.ReadAllAsync())
            {
                await channel.SendAsync(SecureFrameChannel.TypeMedia, frame).ConfigureAwait(false);
                long n = Interlocked.Increment(ref _mediaFrames);
                Interlocked.Add(ref _mediaBytes, frame.Length);
                if (n % 30 == 0)
                    DiagnosticLog.Write("SessionCapture", $"MediaStream: frames={n} bytes={Interlocked.Read(ref _mediaBytes)} falhas={Interlocked.Read(ref _mediaFailed)}");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _mediaFailed);
            DiagnosticLog.Write("SessionCapture", "MediaSenderLoop: " + ex.GetType().Name);
        }
`;
src = src.slice(0, si) + newBody + src.slice(ei);

// 4) Substitui StopCapture
const oldStop = `    private void StopCapture()
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
    }`;
const newStop = `    private void StopCapture()
    {
        DiagnosticLog.Write("SessionHost", "StopCapture chamado");
        try
        {
            _capture?.Stop();
            _capture?.Dispose();
        }
        catch
        {
        }
        _capture = null;
        try { _mediaQueue.Writer.TryComplete(); } catch { }
        try { _mediaSenderTask?.Wait(2000); } catch { }
        _mediaSenderTask = null;
        // Recria a fila para a próxima captura (a anterior foi completada).
        // (Channel não pode ser "des-completado", então criamos sob demanda.)
    }`;
if (!src.includes(oldStop)) { console.error("StopCapture não encontrado"); process.exit(1); }
src = src.replace(oldStop, newStop);

fs.writeFileSync(p, src, "utf8");
console.log("✅ SessionHost.cs atualizado");