using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RemoteEternal.App.Services;

namespace RemoteEternal.App.Media;

/// <summary>
/// Decodificador de H.264 cru (Annex B) por parser, para streaming em tempo real.
/// Substitui o caminho antigo (demuxer mov + MP4 fragmentado): recebe NAL units por
/// frame via <see cref="FeedPacket"/> e dispara <see cref="VideoFrameReady"/> com o
/// frame BGRA pronto para renderizar. Sem container, sem find_stream_info, sem seek.
/// </summary>
public sealed unsafe class H264StreamDecoder : IDisposable
{
    public event Action<byte[], int, int>? VideoFrameReady; // (bgra, width, height)
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public long VideoFrames { get; private set; }
    public string? LastError { get; private set; }

    private AVCodecContext* _ctx;
    private AVCodecParserContext* _parser;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _sws;
    private byte[] _bgra = Array.Empty<byte>();
    private bool _disposed;
    private readonly object _lock = new();

    public H264StreamDecoder()
    {
        FfmpegLibrary.EnsureLoaded();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new InvalidOperationException("Decoder H.264 não encontrado");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->thread_count = 4;
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY; // baixa latência
        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0) throw new InvalidOperationException("avcodec_open2 h264: " + Err(ret));
        _parser = ffmpeg.av_parser_init((int)AVCodecID.AV_CODEC_ID_H264);
        if (_parser == null) throw new InvalidOperationException("av_parser_init h264 falhou");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        DiagnosticLog.Write("H264StreamDecoder", "decoder H.264 (parser) iniciado");
    }

    /// <summary>Alimenta um NAL Annex B (um frame) para decodificação.</summary>
    public void FeedPacket(byte[] nal, bool isKeyframe, long pts)
    {
        lock (_lock)
        {
            if (_disposed || nal is null || nal.Length == 0) return;
            try
            {
                fixed (byte* pNal = nal)
                {
                    byte* cur = pNal;
                    int remaining = nal.Length;
                    while (remaining > 0)
                    {
                        byte* outBuf = null;
                        int outLen = 0;
                        int used = ffmpeg.av_parser_parse2(_parser, _ctx, &outBuf, &outLen, cur, remaining, 0, 0, 0);
                        cur += used;
                        remaining -= used;
                        if (outLen > 0)
                        {
                            _packet->data = outBuf;
                            _packet->size = outLen;
                            DecodeCurrentPacket();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagnosticLog.Write("H264StreamDecoder", "FeedPacket erro: " + ex.Message);
            }
        }
    }

    private void DecodeCurrentPacket()
    {
        int ret = ffmpeg.avcodec_send_packet(_ctx, _packet);
        if (ret < 0) return;
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) >= 0)
        {
            RenderFrame();
        }
    }

    private void RenderFrame()
    {
        int width = _frame->width;
        int height = _frame->height;
        if (width <= 0 || height <= 0) return;

        if (_bgra.Length != width * height * 4)
            _bgra = new byte[width * height * 4];

        var srcFormat = (AVPixelFormat)_frame->format;
        _sws = ffmpeg.sws_getCachedContext(_sws, width, height, srcFormat,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA, 1, null, null, null);
        if (_sws is null) return;

        fixed (byte* pDst = _bgra)
        {
            byte*[] dstData = { pDst, null, null, null };
            int[] dstLinesize = { width * 4, 0, 0, 0 };
            byte*[] srcData = new byte*[4];
            int[] srcLinesize = new int[4];
            for (uint p = 0; p < 4; p++) { srcData[p] = _frame->data[p]; srcLinesize[p] = _frame->linesize[p]; }
            ffmpeg.sws_scale(_sws, srcData, srcLinesize, 0, height, dstData, dstLinesize);
        }

        if (VideoWidth != width || VideoHeight != height)
        {
            VideoWidth = width;
            VideoHeight = height;
        }
        VideoFrames++;
        try { VideoFrameReady?.Invoke(_bgra, width, height); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_parser != null) { var p = _parser; ffmpeg.av_parser_close(p); _parser = null; }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet != null) { var pk = _packet; ffmpeg.av_packet_free(&pk); _packet = null; }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        }
    }

    private static string Err(int code)
    {
        var b = new byte[256];
        fixed (byte* p = b) { ffmpeg.av_strerror(code, p, 256); return Marshal.PtrToStringAnsi((IntPtr)p) ?? code.ToString(); }
    }
}