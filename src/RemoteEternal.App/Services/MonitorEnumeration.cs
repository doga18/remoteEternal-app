using System.Runtime.InteropServices;

namespace RemoteEternal.App.Services;

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Right { get; init; }
    public required int Bottom { get; init; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsPrimary { get; init; }
}

public static class MonitorEnumeration
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    public static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                result.Add(new MonitorInfo
                {
                    DeviceName = info.DeviceName.TrimStart('\\', '.'),
                    Left = info.Monitor.Left,
                    Top = info.Monitor.Top,
                    Right = info.Monitor.Right,
                    Bottom = info.Monitor.Bottom,
                    IsPrimary = (info.Flags & 1) != 0
                });
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static MonitorInfo? Find(string deviceName)
    {
        var name = deviceName.TrimStart('\\', '.');
        return GetMonitors().FirstOrDefault(m =>
            string.Equals(m.DeviceName, name, StringComparison.OrdinalIgnoreCase));
    }
}
