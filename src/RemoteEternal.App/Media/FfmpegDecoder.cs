using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RemoteEternal.App.Services;

namespace RemoteEternal.App.Media;

public sealed class MediaBuffer : IDisposable
{
    // Buffer contínuo e SEEKABLE: os dados recebidos são acumulados em segmentos de
    // 64 KB e podem ser lidos em QUALQUER posição já recebida (pos < Length).
    // O demuxer mov do FFmpeg precisa de seek para MP4 fragmentado ao vivo com
    // custom IO: sem poder voltar ao moov/moof já recebidos, av_read_frame trava
    // após o primeiro fragmento e o vídeo congela no primeiro frame (FPS 0).
    private const int SegmentSize = 64 * 1024;
    private readonly object _lock = new();
    private readonly List<byte[]> _segments = new();
    private long _length;
    private bool _closed;
    private bool _wakeAbort;

    public long Length
    {
        get { lock (_lock) return _length; }
    }

    public void Write(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            int written = 0;
            while (written < count)
            {
                long idx = _length + written;
                int segIdx = (int)(idx / SegmentSize);
                while (_segments.Count <= segIdx) _segments.Add(new byte[SegmentSize]);
                var seg = _segments[segIdx];
                int segOff = (int)(idx % SegmentSize);
                int n = Math.Min(SegmentSize - segOff, count - written);
                Array.Copy(data, offset + written, seg, segOff, n);
                written += n;
            }
            _length += count;
            _wakeAbort = false;
            Monitor.PulseAll(_lock);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
            _length = 0;
            _wakeAbort = false; // FIX: não deixa o buffer "envenenado" pelo decoder anterior
            Monitor.PulseAll(_lock);
        }
    }

    public void WakeAbort()
    {
        lock (_lock)
        {
            _wakeAbort = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Limpa o estado de abort, permitindo que um novo decoder use o buffer.</summary>
    public void ResetAbort()
    {
        lock (_lock)
        {
            _wakeAbort = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Lê até <paramref name="max"/> bytes a partir da posição <paramref name="pos"/>.
    /// Bloqueia (ciclos de 200 ms) enquanto a posição ainda não foi recebida.
    /// Retorna 0 em EOF (fechado/abortado).
    /// </summary>
    public int ReadAt(long pos, byte[] dst, int max)
    {
        lock (_lock)
        {
            while (pos >= _length)
            {
                if (_closed || _wakeAbort) return 0;
                Monitor.Wait(_lock, 200);
            }
            long available = _length - pos;
            int n = (int)Math.Min(max, available);
            int dstOff = 0;
            long cur = pos;
            int remaining = n;
            while (remaining > 0)
            {
                var seg = _segments[(int)(cur / SegmentSize)];
                int segOff = (int)(cur % SegmentSize);
                int take = Math.Min(SegmentSize - segOff, remaining);
                Array.Copy(seg, segOff, dst, dstOff, take);
                dstOff += take;
                cur += take;
                remaining -= take;
            }
            return n;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose() => Close();
}

public unsafe sealed class FfmpegDecoder : IDisposable
{
    public string? LastError { get; private set; }
    public long VideoFrames { get; private set; }
    public long AudioFrames { get; private set; }
    public event Action<string>? ErrorOccurred;
    private void ReportError(string stage, Exception ex)
    {
        LastError = $"{stage}: {ex.Message}";
        try { ErrorOccurred?.Invoke(LastError); } catch { }
    }
    private readonly MediaBuffer _buffer;
    private readonly AVIOContext* _avio;
    private GCHandle _selfHandle;
    private AVFormatContext* _fmt;
    private AVCodecContext* _videoCtx;
    private AVCodecContext* _audioCtx;
    private SwsContext* _sws;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private volatile bool _disposed;
    private bool _eofLogged;
    private Thread? _thread;
    private readonly ManualResetEventSlim _threadDone = new();
    private byte[] _rgba = Array.Empty<byte>();

    // Diagnóstico observacional (não altera o fluxo de decodificação): salva os
    // primeiros bytes de mídia recebidos em %APPDATA%\RemoteEternal\media-dump.bin
    // e registra o hex inicial no DiagnosticLog. O dump é feito no ReadCallback
    // para não consumir dados do MediaBuffer nem interferir no avformat_open_input.
    private static readonly string MediaDumpPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteEternal", "media-dump.bin");
    private const int MediaDumpLimit = 512 * 1024;
    private FileStream? _mediaDumpStream;
    private int _mediaDumpWritten;
    private bool _mediaDumpDone;

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public int AudioSampleRate { get; private set; }
    public int AudioChannels { get; private set; }

    public event Action<byte[], int, int>? VideoFrameReady;
    public event Action<byte[], int, int>? AudioReady;

    private static readonly avio_alloc_context_read_packet ReadFunc = ReadCallback;

    public FfmpegDecoder(MediaBuffer buffer)
    {
        _buffer = buffer;
        FfmpegLibrary.EnsureLoaded();

        _selfHandle = GCHandle.Alloc(this);
        byte* avioBuffer = (byte*)ffmpeg.av_malloc(64 * 1024);
        // NON-SEEKABLE (sem callback de seek): para MP4 fragmentado ao vivo, o demuxer
        // mov lê os fragmentos SEQUENCIALMENTE (ftyp/moov/moof/mdat) e bloqueia na
        // leitura quando alcança o fim dos dados recebidos (ReadAt), continuando quando
        // novos dados chegam. Um stream seekable fazia o demuxer escanear a stream via
        // seeks (lento) e causava "moov atom not found" / "Invalid data".
        _avio = ffmpeg.avio_alloc_context(avioBuffer, 64 * 1024, 0, (void*)GCHandle.ToIntPtr(_selfHandle), ReadFunc, null, null);
        if (_avio is null) throw new InvalidOperationException("Falha ao criar AVIOContext");
    }

    public void Start()
    {
        DiagnosticLog.Write("FfmpegDecoder", $"decoder Start (app {RemoteEternal.App.Services.AppState.AppVersion})");
        var fmt = ffmpeg.avformat_alloc_context();
        fmt->pb = _avio;
        fmt->flags |= 0x80; // AVFMT_FLAG_CUSTOM_IO
        fmt->probesize = 1024 * 1024;
        fmt->max_analyze_duration = 5 * AV_TIME_BASE;

        // O host (ScreenRecorderLib) produz MP4 fragmentado (ftyp/moov/moof/mdat) e o
        // stream chega via custom IO em blocos parciais espaçados no tempo. A detecção
        // automática de formato (format = null) depende do probe nos primeiros bytes e
        // pode falhar com "Invalid data found when processing input" nesse fluxo.
        // Forçar o demuxer mov evita o probe e usa a leitura com wait do MediaBuffer
        // (ReadCallback com wait:true acumula os blocos até o header estar completo).
        var inputFormat = ffmpeg.av_find_input_format("mov");
        int ret;
        if (inputFormat is null)
        {
            // Fallback para builds sem o demuxer mov: mantém a detecção automática.
            ret = ffmpeg.avformat_open_input(&fmt, null, null, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_open_input: {Error(ret)}");
        }
        else
        {
            ret = ffmpeg.avformat_open_input(&fmt, null, inputFormat, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_open_input: {Error(ret)}");
        }
        _fmt = fmt;

        // Usa os streams lidos do moov SEM avformat_find_stream_info: numa stream ao
        // vivo, find_stream_info fica BLOQUEADO analisando dados que chegam devagar do
        // host (~16 s de espera!), acumulando um backlog enorme de vídeo (delay gigante).
        // O moov do MP4 fragmentado já traz codecpar completo (codec, resolução,
        // extradata SPS/PPS). Só chama find_stream_info como fallback se o moov não
        // trouxer um stream de vídeo.
        bool moovHasVideo = false;
        for (int i = 0; i < _fmt->nb_streams; i++)
            if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) { moovHasVideo = true; break; }
        if (!moovHasVideo)
        {
            ret = ffmpeg.avformat_find_stream_info(_fmt, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info: {Error(ret)}");
        }
        else
        {
            DiagnosticLog.Write("FfmpegDecoder", "usando streams do moov (find_stream_info pulado)");
        }

        for (int i = 0; i < _fmt->nb_streams; i++)
        {
            var stream = _fmt->streams[i];
            var par = stream->codecpar;
            if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStream < 0)
            {
                _videoStream = i;
                var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec is not null)
                {
                    _videoCtx = ffmpeg.avcodec_alloc_context3(codec);
                    ffmpeg.avcodec_parameters_to_context(_videoCtx, par);
                    _videoCtx->thread_count = 4;
                    ret = ffmpeg.avcodec_open2(_videoCtx, codec, null);
                    if (ret < 0)
                    {
                        var vc = _videoCtx;
                        ffmpeg.avcodec_free_context(&vc);
                        _videoCtx = null;
                    }
                }
            }
            else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStream < 0)
            {
                _audioStream = i;
                var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec is not null)
                {
                    _audioCtx = ffmpeg.avcodec_alloc_context3(codec);
                    ffmpeg.avcodec_parameters_to_context(_audioCtx, par);
                    ret = ffmpeg.avcodec_open2(_audioCtx, codec, null);
                    if (ret < 0)
                    {
                        var ac = _audioCtx;
                        ffmpeg.avcodec_free_context(&ac);
                        _audioCtx = null;
                    }
                    else
                    {
                        AudioSampleRate = _audioCtx->sample_rate;
                        AudioChannels = Math.Max(1, _audioCtx->ch_layout.nb_channels);
                    }
                }
            }
        }

        if (_videoCtx is null) throw new InvalidOperationException("Stream de vídeo não encontrado");

        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "FfmpegDecoder" };
        _thread.Start();
    }

    private const long AV_TIME_BASE = 1_000_000;
    private const int AVERROR_EOF = -541478725;
    private const int EAGAIN = -11;

    private void DecodeLoop()
    {
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        long packets = 0, videoPackets = 0, audioPackets = 0, videoFrames = 0, audioFrames = 0, errors = 0;
        var lastErrorLog = DateTime.MinValue;
        try
        {
            DiagnosticLog.Write("FfmpegDecoder", "DecodeLoop iniciado");
            while (!_disposed)
            {
                int ret = ffmpeg.av_read_frame(_fmt, packet);
                if (ret < 0)
                {
                    errors++;
                    if (ret == AVERROR_EOF || _disposed) break;
                    // Loga erro persistente (máx 1x por 2s)
                    if ((DateTime.UtcNow - lastErrorLog).TotalSeconds >= 2)
                    {
                        lastErrorLog = DateTime.UtcNow;
                        DiagnosticLog.Write("FfmpegDecoder", $"av_read_frame erro #{errors}: {Error(ret)} (pkts={packets} vf={videoFrames} af={audioFrames})");
                    }
                    continue;
                }
                packets++;
                if (packet->stream_index == _videoStream && _videoCtx is not null)
                {
                    videoPackets++;
                    DecodeVideo(packet, frame);
                }
                else if (packet->stream_index == _audioStream && _audioCtx is not null)
                {
                    audioPackets++;
                    DecodeAudio(packet, frame);
                }
                ffmpeg.av_packet_unref(packet);

                // Log de progresso a cada 100 pacotes
                if (packets % 100 == 0)
                    DiagnosticLog.Write("FfmpegDecoder", $"pkts={packets} vp={videoPackets} vf={videoFrames} ap={audioPackets} af={audioFrames} errs={errors}");
            }
            DiagnosticLog.Write("FfmpegDecoder", $"DecodeLoop fim: pkts={packets} vf={videoFrames} af={audioFrames} errs={errors}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("FfmpegDecoder", $"DecodeLoop EXCEÇÃO: {ex.GetType().Name}: {ex.Message}");
            ReportError("DecodeLoop", ex);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            _threadDone.Set();
        }
    }

    private void DecodeVideo(AVPacket* packet, AVFrame* frame)
    {
        int ret = ffmpeg.avcodec_send_packet(_videoCtx, packet);
        if (ret < 0)
        {
            DiagnosticLog.Write("FfmpegDecoder", $"DecodeVideo send_packet falhou: {Error(ret)}");
            return;
        }
        while (ffmpeg.avcodec_receive_frame(_videoCtx, frame) >= 0)
        {
            int width = frame->width;
            int height = frame->height;
            if (width <= 0 || height <= 0) continue;

            if (_rgba.Length != width * height * 4)
                _rgba = new byte[width * height * 4];

            var srcFormat = (AVPixelFormat)frame->format;
            _sws = ffmpeg.sws_getCachedContext(_sws, width, height, srcFormat,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA, 1, null, null, null);
            if (_sws is null) continue;

            fixed (byte* pDst = _rgba)
            {
                byte*[] dstData = { pDst, null, null, null };
                int[] dstLinesize = { width * 4, 0, 0, 0 };
                ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, height, dstData, dstLinesize);
            }

            if (VideoWidth != width || VideoHeight != height)
            {
                VideoWidth = width;
                VideoHeight = height;
            }
            if (VideoFrames == 0)
                DiagnosticLog.Write("FfmpegDecoder", $"Primeiro frame de vídeo decodificado: {width}x{height} fmt={srcFormat}");
            VideoFrames++;
            VideoFrameReady?.Invoke(_rgba, width, height);
        }
    }

    private void DecodeAudio(AVPacket* packet, AVFrame* frame)
    {
        int ret = ffmpeg.avcodec_send_packet(_audioCtx, packet);
        if (ret < 0) return;
        while (ffmpeg.avcodec_receive_frame(_audioCtx, frame) >= 0)
        {
            var fmt = (AVSampleFormat)frame->format;
            int channels = Math.Max(1, _audioCtx->ch_layout.nb_channels);
            int samples = frame->nb_samples;
            if (samples <= 0) continue;

            byte[] pcm;
            if (fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP || fmt == AVSampleFormat.AV_SAMPLE_FMT_FLT)
            {
                pcm = new byte[samples * channels * 2];
                bool planar = fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                for (int s = 0; s < samples; s++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float v = planar
                            ? ((float*)frame->extended_data[c])[s]
                            : ((float*)frame->extended_data[0])[s * channels + c];
                        short sv = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                        pcm[(s * channels + c) * 2] = (byte)(sv & 0xFF);
                        pcm[(s * channels + c) * 2 + 1] = (byte)((sv >> 8) & 0xFF);
                    }
                }
            }
            else if (fmt == AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                int bytes = samples * channels * 2;
                pcm = new byte[bytes];
                Marshal.Copy((IntPtr)frame->extended_data[0], pcm, 0, bytes);
            }
            else
            {
                continue;
            }

            AudioSampleRate = _audioCtx->sample_rate;
            AudioChannels = channels;
            AudioReady?.Invoke(pcm, AudioSampleRate, channels);
        }
    }

    private static int ReadCallback(void* opaque, byte* buffer, int bufferSize)
    {
        var self = (FfmpegDecoder?)GCHandle.FromIntPtr((IntPtr)opaque).Target;
        if (self is null || self._disposed) return AVERROR_EOF;
        long pos = self._avio->pos;
        byte[] tmp = new byte[bufferSize];
        int n = self._buffer.ReadAt(pos, tmp, bufferSize);
        if (n <= 0)
        {
            if (!self._eofLogged)
            {
                self._eofLogged = true;
                DiagnosticLog.Write("FfmpegDecoder", $"ReadCallback EOF/sem dados em pos={pos} (len={self._buffer.Length})");
            }
            return AVERROR_EOF;
        }
        Marshal.Copy(tmp, 0, (IntPtr)buffer, n);
        self.RecordMediaDump(tmp, n);
        return n;
    }

    /// <summary>
    /// Diagnóstico observacional: grava os primeiros 512 KB do fluxo de mídia em
    /// <c>%APPDATA%\RemoteEternal\media-dump.bin</c> e registra o hex da primeira
    /// leitura. Nunca lança e nunca altera o retorno da leitura.
    /// </summary>
    private void RecordMediaDump(byte[] data, int count)
    {
        try
        {
            if (_mediaDumpStream is null && !_mediaDumpDone)
            {
                string dir = Path.GetDirectoryName(MediaDumpPath) ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _mediaDumpStream = new FileStream(MediaDumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                DiagnosticLog.Write("FfmpegDecoder",
                    $"primeira leitura: {count} bytes; hex16={BitConverter.ToString(data, 0, Math.Min(16, count))}");
            }

            if (_mediaDumpDone || _mediaDumpStream is null) return;

            int remaining = MediaDumpLimit - _mediaDumpWritten;
            if (remaining <= 0)
            {
                CompleteMediaDump();
                return;
            }

            int toWrite = Math.Min(count, remaining);
            _mediaDumpStream.Write(data, 0, toWrite);
            _mediaDumpWritten += toWrite;

            if (_mediaDumpWritten >= MediaDumpLimit)
            {
                CompleteMediaDump();
            }
        }
        catch
        {
            // Nunca interfira na decodificação por causa do diagnóstico.
            try { _mediaDumpStream?.Dispose(); } catch { }
            _mediaDumpStream = null;
            _mediaDumpDone = true;
        }
    }

    private void CompleteMediaDump()
    {
        try
        {
            _mediaDumpStream?.Dispose();
        }
        catch
        {
        }
        _mediaDumpStream = null;
        _mediaDumpDone = true;
        DiagnosticLog.Write("FfmpegDecoder", $"media-dump salvo: {MediaDumpPath} ({_mediaDumpWritten} bytes)");
    }

    private static string Error(int code)
    {
        var buf = new byte[256];
        fixed (byte* p = buf)
        {
            ffmpeg.av_strerror(code, p, 256);
            return Marshal.PtrToStringAnsi((IntPtr)p) ?? code.ToString();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Libera o dump de diagnóstico caso a sessão termine antes de completar.
        if (!_mediaDumpDone)
        {
            try { _mediaDumpStream?.Dispose(); } catch { }
            _mediaDumpStream = null;
            _mediaDumpDone = true;
            DiagnosticLog.Write("FfmpegDecoder", $"media-dump parcial fechado: {MediaDumpPath} ({_mediaDumpWritten} bytes)");
        }
        _buffer.WakeAbort();
        bool threadExited = _threadDone.Wait(3000);
        if (!threadExited) return; // thread não saiu; evita liberar memória em uso
        // FIX: reseta o abort do buffer compartilhado para não envenenar o próximo decoder.
        _buffer.ResetAbort();
        if (_fmt is not null)
        {
            var fmt = _fmt;
            ffmpeg.avformat_close_input(&fmt);
            _fmt = null;
        }
        if (_videoCtx is not null)
        {
            var vc = _videoCtx;
            ffmpeg.avcodec_free_context(&vc);
            _videoCtx = null;
        }
        if (_audioCtx is not null)
        {
            var ac = _audioCtx;
            ffmpeg.avcodec_free_context(&ac);
            _audioCtx = null;
        }
        if (_sws is not null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
        if (_avio is not null)
        {
            var avio = _avio;
            var buf = avio->buffer;
            ffmpeg.avio_context_free(&avio);
            if (buf is not null) ffmpeg.av_free(buf);
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }
}
