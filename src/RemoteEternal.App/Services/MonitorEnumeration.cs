using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

    private static readonly Regex DisplayIdentityPattern = new(
        @"(?:^|[\\/])DISPLAY(?<number>[0-9]+)(?:$|[\\/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Canonicalize(string deviceName)
    {
        if (!TryGetDisplayIdentity(deviceName, out int number))
            return deviceName.Trim();
        return $"\\\\.\\DISPLAY{number}";
    }

    public static string FriendlyName(string deviceName)
    {
        return TryGetDisplayIdentity(deviceName, out int number)
            ? $"DISPLAY{number}"
            : deviceName.Trim();
    }

    public static bool SameDisplay(string left, string right)
    {
        if (!TryGetDisplayIdentity(left, out int leftNumber) ||
            !TryGetDisplayIdentity(right, out int rightNumber))
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        return leftNumber == rightNumber;
    }

    private static bool TryGetDisplayIdentity(string deviceName, out int number)
    {
        number = 0;
        var match = DisplayIdentityPattern.Match(deviceName.Trim());
        return match.Success && int.TryParse(match.Groups["number"].Value, out number);
    }

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
                    DeviceName = Canonicalize(info.DeviceName),
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
        return GetMonitors().FirstOrDefault(m => SameDisplay(m.DeviceName, deviceName));
    }
}
