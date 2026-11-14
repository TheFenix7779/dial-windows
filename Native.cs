using System;
using System.Runtime.InteropServices;

namespace DialWin;

internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_HOTKEY = 0x0312;
    public const uint LLKHF_INJECTED = 0x10;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

/// <summary>Synthetic keyboard and mouse input.</summary>
internal static class Send
{
    public const ushort Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12,
        Esc = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28,
        LWin = 0x5B, F5 = 0x74, VolumeMute = 0xAD, PlayPause = 0xB3,
        A = 0x41, T = 0x54, W = 0x57, Y = 0x59, Z = 0x5A;

    static bool IsExtended(ushort vk) =>
        (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C;

    static Native.INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? 0x0002u : 0u;
        if (IsExtended(vk)) flags |= 0x0001;
        return new Native.INPUT
        {
            type = 1,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = flags } }
        };
    }

    static void Dispatch(Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

    public static void Hold(ushort vk) => Dispatch(new[] { Key(vk, false) });
    public static void Release(ushort vk) => Dispatch(new[] { Key(vk, true) });

    /// <summary>Press the keys in order, then release them in reverse: a chord like Ctrl+Shift+T.</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new Native.INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], true);
        }
        Dispatch(inputs);
    }

    public static void Wheel(int delta)
    {
        var input = new Native.INPUT
        {
            type = 0,
            u = new Native.InputUnion { mi = new Native.MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = 0x0800 } }
        };
        Dispatch(new[] { input });
    }
}
