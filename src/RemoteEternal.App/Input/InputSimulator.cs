using System.Runtime.InteropServices;

namespace RemoteEternal.App.Input;

public static class InputSimulator
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheelFlag = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    private const uint KeyUp = 0x0002;
    private const uint KeyScancode = 0x0008;
    private const uint KeyExtended = 0x0001;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void Send(Input input)
    {
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    public static void MoveMouseAbsolute(int virtualDesktopX, int virtualDesktopY)
    {
        // SendInput com MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK espera coordenadas
        // normalizadas de 0 a 65535 sobre o retângulo do desktop virtual, não pixels.
        int vx = GetSystemMetrics(SmXVirtualScreen);
        int vy = GetSystemMetrics(SmYVirtualScreen);
        int vw = GetSystemMetrics(SmCxVirtualScreen);
        int vh = GetSystemMetrics(SmCyVirtualScreen);
        if (vw <= 0 || vh <= 0)
        {
            // Fallback defensivo: métricas do desktop virtual indisponíveis; usa a tela primária.
            vx = 0;
            vy = 0;
            vw = GetSystemMetrics(SmCxScreen);
            vh = GetSystemMetrics(SmCyScreen);
            if (vw <= 0 || vh <= 0) return;
        }
        int dx = (int)((virtualDesktopX - vx) * 65535.0 / vw);
        int dy = (int)((virtualDesktopY - vy) * 65535.0 / vh);
        dx = Math.Clamp(dx, 0, 65535);
        dy = Math.Clamp(dy, 0, 65535);
        var input = new Input
        {
            Type = InputMouse,
            U = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = MouseMove | MouseAbsolute | MouseVirtualDesk
                }
            }
        };
        Send(input);
    }

    public static void MouseButton(byte button, bool down)
    {
        uint flags = button switch
        {
            1 => down ? MouseLeftDown : MouseLeftUp,
            2 => down ? MouseRightDown : MouseRightUp,
            3 => down ? MouseMiddleDown : MouseMiddleUp,
            _ => 0
        };
        if (flags == 0) return;
        var input = new Input { Type = InputMouse, U = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
        Send(input);
    }

    public static void MouseWheel(int delta)
    {
        var input = new Input
        {
            Type = InputMouse,
            U = new InputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = (uint)delta,
                    Flags = MouseWheelFlag
                }
            }
        };
        Send(input);
    }

    public static void KeyEvent(ushort vk, bool down)
    {
        uint scan = MapVirtualKeyEx(vk, 4, IntPtr.Zero);
        bool extended = (scan & 0xE000) != 0;
        scan &= 0xFF;
        var input = new Input
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Vk = 0,
                    Scan = (ushort)scan,
                    Flags = (down ? 0 : KeyUp) | KeyScancode | (extended ? KeyExtended : 0)
                }
            }
        };
        Send(input);
    }

    public static void ReleaseAllModifiers()
    {
        ReleaseIfDown(0xA2, 0x1D);  // Ctrl esquerdo
        ReleaseIfDown(0xA3, 0xE01D); // Ctrl direito
        ReleaseIfDown(0xA0, 0x2A);  // Shift esquerdo
        ReleaseIfDown(0xA1, 0x36);  // Shift direito
        ReleaseIfDown(0xA4, 0x38);  // Alt esquerdo
        ReleaseIfDown(0xA5, 0xE038); // Alt direito
        ReleaseIfDown(0x5B, 0xE05B); // Win esquerdo
    }

    private static void ReleaseIfDown(int vk, uint scan)
    {
        if ((GetAsyncKeyState(vk) & 0x8000) == 0) return;
        var input = new Input
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Vk = 0,
                    Scan = (ushort)(scan & 0xFF),
                    Flags = KeyUp | KeyScancode | ((scan & 0xE000) != 0 ? KeyExtended : 0)
                }
            }
        };
        Send(input);
    }
}
