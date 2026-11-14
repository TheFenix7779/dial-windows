using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DialWin;

/// <summary>
/// Windows pops up its volume overlay on every dial turn, and it gets in the way of the next click.
/// While the dial isn't controlling volume, hide any small shell pop-up that appears after a turn.
/// </summary>
internal static class OsdSuppressor
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static readonly HashSet<string> ShellClasses = new()
    {
        "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "NativeHWNDHost",
    };
    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "ShellHost", "SndVol", "TextInputHost",
    };

    static readonly Dictionary<uint, string> ProcessNames = new();
    static readonly HashSet<string> Reported = new();
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    static long _until;

    static OsdSuppressor() => Timer.Tick += (_, _) =>
    {
        Sweep();
        if (Environment.TickCount64 > _until) Timer.Stop();
    };

    /// <summary>Call after a turn that shouldn't show a volume overlay. Sweeps for the next 1.5 s.</summary>
    public static void Pulse()
    {
        _until = Environment.TickCount64 + 1500;
        Sweep();
        if (!Timer.IsEnabled) Timer.Start();
    }

    static void Sweep()
    {
        WinApi.EnumWindows((h, _) =>
        {
            if (!WinApi.IsWindowVisible(h)) return true;

            // Windows 11 draws its volume pill inside TextInputHost's full-screen "Windows Input Experience" window
            WinApi.GetWindowThreadProcessId(h, out uint ipid);
            if (NameOf(ipid).Equals("TextInputHost", StringComparison.OrdinalIgnoreCase))
            {
                WinApi.DwmGetWindowAttribute(h, 14, out int c2, sizeof(int));
                var t2 = new StringBuilder(128);
                WinApi.GetWindowText(h, t2, t2.Capacity);
                if (Reported.Add($"TIH|{t2}|cloak={c2}")) Log.Write($"TextInputHost window '{t2}' cloak={c2}");
                if (c2 == 0) { WinApi.ShowWindow(h, 0); Log.Write("Hid TextInputHost window"); }
                return true;
            }

            var cls = new StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);

            WinApi.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int));
            if (cloaked != 0) return true;

            GetWindowRect(h, out var r);
            int w = r.R - r.L, hgt = r.B - r.T;
            bool big = w > 700 || hgt > 400;
            if (w <= 1 || hgt <= 1) return true;

            WinApi.GetWindowThreadProcessId(h, out uint pid);
            string proc = NameOf(pid);
            if (pid == Environment.ProcessId) return true;

            var title = new StringBuilder(128);
            WinApi.GetWindowText(h, title, title.Capacity);
            string key = $"{cls}|{title}|{w}x{hgt}@{r.L},{r.T}|{proc}";
            bool shell = ShellClasses.Contains(cls.ToString()) || ShellProcesses.Contains(proc);
            if (Reported.Add(key)) Log.Write($"Small window after turn: {key} shell={shell}");
            if (!shell || big) return true; // big shell windows are only logged for now

            WinApi.ShowWindow(h, 0 /* SW_HIDE */);
            return true;
        }, IntPtr.Zero);
    }

    static string NameOf(uint pid)
    {
        if (ProcessNames.TryGetValue(pid, out var name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { name = ""; }
        ProcessNames[pid] = name;
        return name;
    }
}
