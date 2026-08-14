using NAudio.Wave;

namespace RemoteEternal.App.Media;

public sealed class AudioPlayer : IDisposable
{
    private readonly object _lock = new();
    private BufferedWaveProvider? _provider;
    private WaveOutEvent? _output;
    private WaveFormat? _format;

    public void SetFormat(int sampleRate, int channels)
    {
        lock (_lock)
        {
            var fmt = new WaveFormat(sampleRate, 16, channels);
            if (_format is not null && _format.SampleRate == sampleRate && _format.Channels == channels)
                return;
            _format = fmt;
            RestartLocked();
        }
    }

    public void AddSamples(byte[] pcm, int offset, int count)
    {
        lock (_lock)
        {
            if (_provider is null) return;
            if (_provider.BufferedDuration.TotalSeconds > 3)
            {
                _provider.ClearBuffer();
            }
            _provider.AddSamples(pcm, offset, count);
        }
    }

    public void Restart()
    {
        lock (_lock)
        {
            if (_format is null) return;
            RestartLocked();
        }
    }

    private void RestartLocked()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _provider = new BufferedWaveProvider(_format!)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            _output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 2 };
            _output.Init(_provider);
            _output.Play();
        }
        catch
        {
            _provider = null;
            _output = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _provider = null;
        }
    }
}
