using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DialWin;

/// <summary>One slice of a wheel: a mode, or an open window.</summary>
internal sealed record WheelItem(string Label, string Short, ImageSource? Icon, IntPtr Window = default);

internal static class WinApi
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}

internal static class AppWindows
{
    const int MaxApps = 12;
    static readonly Dictionary<string, ImageSource?> IconCache = new();

    /// <summary>Open, visible, switchable windows, like the ones on the taskbar.</summary>
    public static List<WheelItem> List()
    {
        var items = new List<WheelItem>();
        int self = Environment.ProcessId;

        WinApi.EnumWindows((h, _) =>
        {
            if (items.Count >= MaxApps || !WinApi.IsWindowVisible(h)) return true;
            if (WinApi.GetWindowTextLength(h) == 0) return true;

            WinApi.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int));
            if (cloaked != 0) return true;

            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            bool toolWindow = (ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & 0x40000 /* APPWINDOW */) == 0;
            if (toolWindow || WinApi.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

            WinApi.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;

            var sb = new StringBuilder(256);
            WinApi.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (title == "Program Manager") return true;

            string name = title;
            string? path = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                path = p.MainModule?.FileName;
                name = p.ProcessName;
            }
            catch { }

            items.Add(new WheelItem(title.Length > 48 ? title[..48] + "…" : title, name, IconFor(path), h));
            return true;
        }, IntPtr.Zero);

        return items;
    }

    static ImageSource? IconFor(string? path)
    {
        if (path == null) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
        }
        catch { }
        IconCache[path] = source;
        return source;
    }

    public static void Activate(IntPtr window)
    {
        if (WinApi.IsIconic(window)) WinApi.ShowWindow(window, 9 /* SW_RESTORE */);

        // Windows only lets the foreground app's input thread change focus, so borrow it briefly.
        uint fgThread = WinApi.GetWindowThreadProcessId(WinApi.GetForegroundWindow(), out _);
        uint ours = WinApi.GetCurrentThreadId();
        bool attached = fgThread != ours && WinApi.AttachThreadInput(ours, fgThread, true);
        WinApi.BringWindowToTop(window);
        WinApi.SetForegroundWindow(window);
        if (attached) WinApi.AttachThreadInput(ours, fgThread, false);

        if (WinApi.GetForegroundWindow() != window)
        {
            WinApi.SwitchToThisWindow(window, true); // ignores the foreground lock
            Log.Write("Activate: first attempt refused, used SwitchToThisWindow");
        }
        Log.Write($"Activate: foreground is now the target = {WinApi.GetForegroundWindow() == window}");

        // some apps (browsers) grab focus back or ignore the first request: check again shortly
        var recheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        recheck.Tick += (_, _) =>
        {
            recheck.Stop();
            bool ok = WinApi.GetForegroundWindow() == window;
            Log.Write($"Activate recheck: in front = {ok}");
            if (ok) return;
            if (WinApi.IsIconic(window)) WinApi.ShowWindow(window, 9);
            WinApi.SwitchToThisWindow(window, true);
        };
        recheck.Start();
    }
}
