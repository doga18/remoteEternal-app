using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace RemoteEternal.App.Media;

public sealed class MediaBuffer : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly Queue<byte[]> _free = new();
    private bool _closed;
    private bool _wakeAbort;

    public void Write(byte[] data, int offset, int count)
    {
        byte[] chunk = TakeFree(count);
        Array.Copy(data, offset, chunk, 0, count);
        lock (_lock)
        {
            _wakeAbort = false;
            _chunks.Enqueue(chunk);
            Monitor.PulseAll(_lock);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_chunks.Count > 0) _free.Enqueue(_chunks.Dequeue());
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

    public int Read(byte[] dst, int max, bool wait)
    {
        lock (_lock)
        {
            while (_chunks.Count == 0)
            {
                if (_closed || _wakeAbort) return 0;
                if (!wait) return -1;
                Monitor.Wait(_lock, 200);
            }
            byte[] chunk = _chunks.Peek();
            int n = Math.Min(chunk.Length, max);
            Array.Copy(chunk, 0, dst, 0, n);
            if (n == chunk.Length)
            {
                _chunks.Dequeue();
                _free.Enqueue(chunk);
            }
            else
            {
                var rest = TakeFree(chunk.Length - n);
                Array.Copy(chunk, n, rest, 0, chunk.Length - n);
                _free.Enqueue(chunk);
                var list = new List<byte[]> { rest };
                list.AddRange(_chunks.Skip(1));
                _chunks.Clear();
                foreach (var c in list) _chunks.Enqueue(c);
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

    private byte[] TakeFree(int minSize)
    {
        lock (_lock)
        {
            byte[]? best = null;
            while (_free.Count > 0)
            {
                var c = _free.Dequeue();
                if (c.Length >= minSize) { best = c; break; }
            }
            return best ?? new byte[Math.Max(minSize, 64 * 1024)];
        }
    }

    public void Dispose() => Close();
}

public unsafe sealed class FfmpegDecoder : IDisposable
{
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
    private Thread? _thread;
    private readonly ManualResetEventSlim _threadDone = new();
    private byte[] _rgba = Array.Empty<byte>();

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public int AudioSampleRate { get; private set; }
    public int AudioChannels { get; private set; }

    public event Action<byte[], int, int>? VideoFrameReady;
    public event Action<byte[], int, int>? AudioReady;

    private static readonly avio_alloc_context_read_packet ReadFunc = ReadCallback;
    private static readonly avio_alloc_context_seek SeekFunc = SeekCallback;

    public FfmpegDecoder(MediaBuffer buffer)
    {
        _buffer = buffer;
        FfmpegLibrary.EnsureLoaded();

        _selfHandle = GCHandle.Alloc(this);
        byte* avioBuffer = (byte*)ffmpeg.av_malloc(64 * 1024);
        _avio = ffmpeg.avio_alloc_context(avioBuffer, 64 * 1024, 0, (void*)GCHandle.ToIntPtr(_selfHandle), ReadFunc, null, SeekFunc);
        if (_avio is null) throw new InvalidOperationException("Falha ao criar AVIOContext");
    }

    public void Start()
    {
        var fmt = ffmpeg.avformat_alloc_context();
        fmt->pb = _avio;
        fmt->flags |= 0x80; // AVFMT_FLAG_CUSTOM_IO
        fmt->probesize = 1024 * 1024;
        fmt->max_analyze_duration = 5 * AV_TIME_BASE;

        int ret = ffmpeg.avformat_open_input(&fmt, null, null, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_open_input: {Error(ret)}");
        _fmt = fmt;

        ret = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info: {Error(ret)}");

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
        try
        {
            while (!_disposed)
            {
                int ret = ffmpeg.av_read_frame(_fmt, packet);
                if (ret < 0)
                {
                    if (ret == AVERROR_EOF || _disposed) break;
                    continue;
                }
                if (packet->stream_index == _videoStream && _videoCtx is not null)
                {
                    DecodeVideo(packet, frame);
                }
                else if (packet->stream_index == _audioStream && _audioCtx is not null)
                {
                    DecodeAudio(packet, frame);
                }
                ffmpeg.av_packet_unref(packet);
            }
        }
        catch
        {
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
        if (ret < 0) return;
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
        byte[] tmp = new byte[bufferSize];
        int n = self._buffer.Read(tmp, bufferSize, wait: true);
        if (n <= 0) return AVERROR_EOF;
        Marshal.Copy(tmp, 0, (IntPtr)buffer, n);
        return n;
    }

    private static long SeekCallback(void* opaque, long offset, int whence)
    {
        return -1;
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
        _buffer.WakeAbort();
        if (!_threadDone.Wait(3000)) return; // thread não saiu; evita liberar memória em uso
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
