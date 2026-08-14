using System.Buffers.Binary;
using System.IO;
using RemoteEternal.Core.Protocol;

namespace RemoteEternal.Core.Protocol;

public static class SessionControlTypes
{
    public const string Hello = "hello";
    public const string Start = "start";
    public const string SwitchDisplay = "switchDisplay";
    public const string MediaRestart = "mediaRestart";
    public const string End = "end";
    public const string Error = "error";
}

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, int Left, int Top);
public sealed record SessionHello(string DeviceName, DisplayInfo[] Displays, int DefaultDisplayIndex);
public sealed record SessionStart(string DisplayId, int Fps, int BitrateKbps, int Quality, bool AudioEnabled);
public sealed record SessionSwitchDisplay(string DisplayId);
public sealed record SessionMediaRestart(string Reason);
public sealed record SessionEnd(string Reason);

public static class InputEvents
{
    public const byte MouseMove = 1;
    public const byte MouseDown = 2;
    public const byte MouseUp = 3;
    public const byte MouseWheel = 4;
    public const byte KeyDown = 5;
    public const byte KeyUp = 6;
}

public static class InputEncoder
{
    public static byte[] MouseMove(int x, int y)
    {
        byte[] p = new byte[9];
        p[0] = InputEvents.MouseMove;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(1), x);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(5), y);
        return p;
    }

    public static byte[] MouseButton(bool down, byte button)
    {
        byte[] p = new byte[3];
        p[0] = down ? InputEvents.MouseDown : InputEvents.MouseUp;
        p[1] = button;
        return p;
    }

    public static byte[] MouseWheel(int delta)
    {
        byte[] p = new byte[5];
        p[0] = InputEvents.MouseWheel;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(1), delta);
        return p;
    }

    public static byte[] KeyEvent(bool down, ushort vk)
    {
        byte[] p = new byte[4];
        p[0] = down ? InputEvents.KeyDown : InputEvents.KeyUp;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), vk);
        return p;
    }
}
