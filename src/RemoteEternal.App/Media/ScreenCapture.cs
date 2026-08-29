using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RemoteEternal.App.Services;
using D3D11Dev = Vortice.Direct3D11.ID3D11Device;
using D3D11Tex = Vortice.Direct3D11.ID3D11Texture2D;
using D3D11Ctx = Vortice.Direct3D11.ID3D11DeviceContext;
using static Vortice.Direct3D11.D3D11;

namespace RemoteEternal.App.Media;

/// <summary>
/// Captura de tela em tempo real com Desktop Duplication + encode H.264 de hardware
/// (h264_nvenc, sem B-frames, baixa latência). Produz NAL H.264 (Annex B) por frame
/// via o evento <see cref="FrameReady"/>. Substitui o ScreenRecorderLib (que gerava
/// MP4 fragmentado com latência de segundos) para streaming em tempo real.
/// </summary>
public sealed unsafe class ScreenCapture : IDisposable
{
    /// <summary>(nalAnnexB, isKeyframe, ptsMs)</summary>
    public event Action<byte[], bool, long>? FrameReady;
    public event Action<string>? Failed;

    private readonly object _sync = new();
    private Thread? _thread;
    private volatile bool _stop;
    private bool _running;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? DeviceName { get; private set; }

    public bool IsRunning => _running;

    public void Start(string? deviceName, int fps, int bitrateKbps)
    {
        lock (_sync)
        {
            if (_running) return;
            _stop = false;
            DeviceName = deviceName;
            _thread = new Thread(() => Run(deviceName, fps, bitrateKbps)) { IsBackground = true, Name = "ScreenCapture" };
            _running = true;
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stop = true;
            _running = false;
        }
        try { _thread?.Join(3000); } catch { }
    }

    public void Dispose() => Stop();

    private void Run(string? deviceName, int fps, int bitrateKbps)
    {
        D3D11Dev? dev = null;
        D3D11Ctx? ctx = null;
        IDXGIOutputDuplication? duplication = null;
        D3D11Tex? staging = null;
        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        IDXGIOutput? output = null;
        IDXGIOutput1? output1 = null;
        AVCodecContext* enc = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        try
        {
            FfmpegLibrary.EnsureLoaded();
            FeatureLevel[] fl = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            var result = D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, fl,
                out D3D11Dev? device, out FeatureLevel _, out D3D11Ctx? context);
            if (result.Failure || device == null || context == null) { ReportFail("D3D11CreateDevice: " + result); return; }
            dev = device!; ctx = context!;

            dxgiDevice = dev.QueryInterface<IDXGIDevice>();
            adapter = dxgiDevice.GetAdapter();
            output = FindOutput(adapter, deviceName);
            if (output == null) { ReportFail("Monitor não encontrado: " + (deviceName ?? "(padrão)")); return; }
            output1 = output.QueryInterface<IDXGIOutput1>();
            duplication = output1.DuplicateOutput(dev);
            var desc = output.Description;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            DiagnosticLog.Write("ScreenCapture", $"capturando {desc.DeviceName} {Width}x{Height} @ {fps}fps {bitrateKbps}kbps");

            DiagnosticLog.Write("ScreenCapture", "step1: criando staging texture...");
            staging = dev.CreateTexture2D(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1, null,
                BindFlags.None, ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            DiagnosticLog.Write("ScreenCapture", "step1 OK: staging criada");


            DiagnosticLog.Write("ScreenCapture", "step2: procurando encoder h264_nvenc...");
            var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
            if (codec == null) { ReportFail("h264_nvenc não encontrado"); return; }
            DiagnosticLog.Write("ScreenCapture", "step2 OK: encoder encontrado");
            enc = ffmpeg.avcodec_alloc_context3(codec);
            enc->width = Width; enc->height = Height;
            // NVENC aceita BGRA diretamente: evita a conversão BGRA->NV12 em CPU
            // (sws_scale), que era o gargalo de FPS.
            enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_BGRA;
            enc->time_base = new AVRational { num = 1, den = fps };
            enc->framerate = new AVRational { num = fps, den = 1 };
            enc->bit_rate = bitrateKbps * 1000L;
            enc->gop_size = fps; // 1 keyframe/s
            enc->max_b_frames = 0; // sem B-frames
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "p1", 0);
            ffmpeg.av_dict_set(&opts, "tune", "ll", 0);
            ffmpeg.av_dict_set(&opts, "zerolatency", "1", 0);
            ffmpeg.av_dict_set(&opts, "rc", "cbr", 0);
            // Repete SPS/PPS em cada keyframe: permite que um cliente que conectou
            // no meio do stream sincronize no próximo keyframe (evita tela preta).
            ffmpeg.av_dict_set(&opts, "repeat-headers", "1", 0);
            ffmpeg.av_dict_set(&opts, "forced-idr", "1", 0);
            DiagnosticLog.Write("ScreenCapture", "step3: abrindo encoder nvenc...");
            int ret = ffmpeg.avcodec_open2(enc, codec, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) { ReportFail("avcodec_open2: " + Err(ret)); return; }

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            frame->width = Width; frame->height = Height;
            ffmpeg.av_frame_get_buffer(frame, 32);
            packet = ffmpeg.av_packet_alloc();

            var swClock = Stopwatch.StartNew();
            long frameIndex = 0;
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / fps);
            var nextFrame = swClock.Elapsed;

            while (!_stop)
            {
                // Ritmo de captura (fps)
                var now = swClock.Elapsed;
                if (now < nextFrame) { Thread.Sleep(1); continue; }
                nextFrame = now + frameInterval;

                var acq = duplication.AcquireNextFrame((uint)Math.Max(1, 1000 / fps), out var frameInfo, out IDXGIResource? resource);
                if (acq.Failure || resource == null) { try { duplication.ReleaseFrame(); } catch { } continue; }
                var tex = resource.QueryInterface<D3D11Tex>();
                ctx.CopyResource(staging, tex);
                resource.Dispose(); tex.Dispose();
                duplication.ReleaseFrame();

                var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int rowPitch = (int)mapped.RowPitch;
                ffmpeg.av_frame_make_writable(frame);
                // Cópia direta BGRA linha a linha (rowPitch -> linesize).
                byte* srcRow = (byte*)mapped.DataPointer;
                byte* dstRow = frame->data[0];
                int dstStride = frame->linesize[0];
                int rowBytes = Math.Min(rowPitch, dstStride);
                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(srcRow, dstRow, dstStride, rowBytes);
                    srcRow += rowPitch;
                    dstRow += dstStride;
                }
                ctx.Unmap(staging, 0);

                frame->pts = frameIndex;
                long ptsMs = swClock.ElapsedMilliseconds;
                ret = ffmpeg.avcodec_send_frame(enc, frame);
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_packet(enc, packet);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;
                    bool isKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    var nal = new byte[packet->size];
                    Marshal.Copy((IntPtr)packet->data, nal, 0, packet->size);
                    try { FrameReady?.Invoke(nal, isKey, ptsMs); } catch { }
                    ffmpeg.av_packet_unref(packet);
                }
                frameIndex++;
            }
        }
        catch (Exception ex)
        {
            ReportFail(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (enc != null) { var e = enc; ffmpeg.avcodec_free_context(&e); }
            staging?.Dispose();
            duplication?.Dispose();
            output1?.Dispose();
            output?.Dispose();
            adapter?.Dispose();
            dxgiDevice?.Dispose();
            ctx?.Dispose();
            dev?.Dispose();
            DiagnosticLog.Write("ScreenCapture", "captura encerrada");
        }
    }

    private static IDXGIOutput? FindOutput(IDXGIAdapter adapter, string? deviceName)
    {
        for (uint i = 0; ; i++)
        {
            var r = adapter.EnumOutputs(i, out IDXGIOutput? output);
            if (r.Failure || output == null) return null;
            if (deviceName == null) return output;
            var name = output.Description.DeviceName;
            if (string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase)) return output;
            output.Dispose();
        }
    }

    private void ReportFail(string msg)
    {
        DiagnosticLog.Write("ScreenCapture", "FALHA: " + msg);
        try { Failed?.Invoke(msg); } catch { }
    }

    private static string Err(int code)
    {
        var b = new byte[256];
        fixed (byte* p = b) { ffmpeg.av_strerror(code, p, 256); return Marshal.PtrToStringAnsi((IntPtr)p) ?? code.ToString(); }
    }
}