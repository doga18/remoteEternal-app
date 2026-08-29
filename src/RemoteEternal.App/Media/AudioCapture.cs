using System;
using System.Threading;
using NAudio.Wave;
using RemoteEternal.App.Services;

namespace RemoteEternal.App.Media;

/// <summary>
/// Captura o áudio do sistema (loopback WASAPI) e o entrega como PCM 16-bit via
/// <see cref="AudioReady"/>. Enviado separadamente do vídeo no pipeline de tempo real.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// <summary>(pcm16le bytes, sampleRate, channels)</summary>
    public event Action<byte[], int, int>? AudioReady;
    public event Action<string>? Failed;

    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private int _channels = 2;

    public bool IsRecording => _capture is not null;

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _channels = _capture.WaveFormat.Channels;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    DiagnosticLog.Write("AudioCapture", "RecordingStopped: " + e.Exception.Message);
            };
            _capture.StartRecording();
            DiagnosticLog.Write("AudioCapture", $"loopback iniciado {_sampleRate}Hz {_channels}ch {_capture.WaveFormat.Encoding}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("AudioCapture", "Falha ao iniciar loopback: " + ex.Message);
            try { Failed?.Invoke(ex.Message); } catch { }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        try
        {
            // e.Buffer contém PCM; se for IEEE float (32-bit), converte para 16-bit.
            byte[] pcm;
            if (_capture!.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && _capture.WaveFormat.BitsPerSample == 32)
            {
                pcm = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded);
            }
            else
            {
                pcm = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
            }
            AudioReady?.Invoke(pcm, _sampleRate, _channels);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("AudioCapture", "OnDataAvailable erro: " + ex.Message);
        }
    }

    private static byte[] ConvertFloatToPcm16(byte[] buffer, int bytesRecorded)
    {
        int samples = bytesRecorded / 4; // 32-bit float
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            float f = BitConverter.ToSingle(buffer, i * 4);
            short s = (short)(Math.Clamp(f, -1f, 1f) * 32767f);
            byte[] b = BitConverter.GetBytes(s);
            pcm[i * 2] = b[0];
            pcm[i * 2 + 1] = b[1];
        }
        return pcm;
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch { }
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}