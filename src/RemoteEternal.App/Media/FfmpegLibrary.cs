using System.IO;
using System.Reflection;
using FFmpeg.AutoGen;

namespace RemoteEternal.App.Media;

public static class FfmpegLibrary
{
    private static bool _initialized;

    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static void EnsureLoaded()
    {
        if (_initialized) return;
        string path = FfmpegPath;
        if (Directory.Exists(path))
        {
            ffmpeg.RootPath = path;
            var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", path + ";" + processPath);
        }
        DynamicallyLoadedBindings.Initialize();
        _initialized = true;
    }
}
