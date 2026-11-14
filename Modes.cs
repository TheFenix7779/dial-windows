using System;
using System.Management;
using System.Windows.Threading;

namespace DialWin;

internal interface IMode
{
    string Id { get; }
    string Label { get; }
    string Short { get; }
    /// <summary>Called when the mode becomes the active one.</summary>
    void Enter();
    /// <summary>Called when the wheel opens or the mode is replaced; undo anything held open.</summary>
    void Exit();
    /// <summary>One detent. Returns text for the HUD, or null to show nothing.</summary>
    string? Turn(int direction, bool fast);
    string? Press();
    /// <summary>True when a press belongs to the mode; otherwise a press plays or pauses media.</summary>
    bool OwnsPress => false;
}

internal sealed class VolumeMode : IMode
{
    readonly VolumeEncoder _encoder;
    readonly Settings _settings;

    public VolumeMode(VolumeEncoder encoder, Settings settings) { _encoder = encoder; _settings = settings; }

    public string Id => "volume";
    public string Label => "Volume";
    public string Short => "Volume";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        var v = _encoder.Current;
        return v is float f ? $"{Math.Round(f * 100)}%" : "Volume";
    }

    public string? Press()
    {
        switch (_settings.VolumePress)
        {
            case "playpause": Send.Chord(Send.PlayPause); return "Play / pause";
            case "mute": Send.Chord(Send.VolumeMute); return "Mute";
            default: return null;
        }
    }
}

/// <summary>Laptop screens only: external monitors don't expose brightness over WMI.</summary>
internal sealed class BrightnessMode : IMode
{
    public string Id => "brightness";
    public string Label => "Brightness";
    public string Short => "Bright";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        try
        {
            int current = Read();
            int next = Math.Clamp(current + direction * (fast ? 10 : 5), 0, 100);
            Write(next);
            return $"{next}%";
        }
        catch (Exception ex)
        {
            Log.Write("Brightness unavailable: " + ex.Message);
            return "Not supported on this display";
        }
    }

    public string? Press() => null;

    static int Read()
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        throw new InvalidOperationException("no brightness-capable display");
    }

    static void Write(int percent)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject o in searcher.Get())
        {
            o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
            return;
        }
    }
}

/// <summary>The model shows a wheel of open windows when this mode is chosen, and again on press.</summary>
internal sealed class AppsMode : IMode
{
    public string Id => "apps";
    public string Label => "Apps";
    public string Short => "Apps";
    public bool OwnsPress => true;
    public void Enter() { }
    public void Exit() { }
    public string? Turn(int direction, bool fast) => null;
    public string? Press() => null;
}

/// <summary>Windows' Mission Control: opens Task View, arrows move, press picks, wheel opening closes it.</summary>
internal sealed class TaskViewMode : IMode
{
    bool _open;

    public string Id => "taskview";
    public string Label => "Task View";
    public string Short => "Tasks";
    public bool OwnsPress => true;
    public void Enter() => Open();
    public void Exit() => Close();

    void Open()
    {
        if (_open) return;
        Send.Chord(Send.LWin, Send.Tab);
        _open = true;
    }

    void Close()
    {
        if (!_open) return;
        Send.Chord(Send.Esc);
        _open = false;
    }

    public string? Turn(int direction, bool fast)
    {
        if (!_open) { Open(); return null; }
        Send.Chord(direction > 0 ? Send.Right : Send.Left);
        return null;
    }

    public string? Press()
    {
        if (!_open) { Open(); return null; }
        Send.Chord(Send.Enter);
        _open = false;
        return null;
    }
}

internal sealed class DesktopsMode : IMode
{
    public string Id => "desktops";
    public string Label => "Desktops";
    public string Short => "Desks";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        Send.Chord(Send.Ctrl, Send.LWin, direction > 0 ? Send.Right : Send.Left);
        return direction > 0 ? "Next desktop" : "Previous desktop";
    }

    public string? Press()
    {
        Send.Chord(Send.LWin, Send.Tab);
        return null;
    }
}

internal sealed class TabsMode : IMode
{
    public string Id => "tabs";
    public string Label => "Tabs";
    public string Short => "Tabs";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        if (direction > 0) Send.Chord(Send.Ctrl, Send.Tab);
        else Send.Chord(Send.Ctrl, Send.Shift, Send.Tab);
        return direction > 0 ? "Next tab" : "Previous tab";
    }

    public string? Press()
    {
        Send.Chord(Send.Ctrl, Send.T);
        return "New tab";
    }
}

internal sealed class ScrollMode : IMode
{
    public string Id => "scroll";
    public string Label => "Scroll";
    public string Short => "Scroll";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        Send.Wheel(-direction * (fast ? 720 : 240)); // turning right scrolls down
        return null;
    }

    public string? Press()
    {
        Send.Chord(Send.PageDown);
        return "Page down";
    }
}

internal sealed class UndoRedoMode : IMode
{
    public string Id => "undo";
    public string Label => "Undo / Redo";
    public string Short => "Undo";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        if (direction < 0) { Send.Chord(Send.Ctrl, Send.Z); return "Undo"; }
        Send.Chord(Send.Ctrl, Send.Y);
        return "Redo";
    }

    public string? Press() => null;
}

internal sealed class SlidesMode : IMode
{
    public string Id => "slides";
    public string Label => "Slides";
    public string Short => "Slides";
    public void Enter() { }
    public void Exit() { }

    public string? Turn(int direction, bool fast)
    {
        Send.Chord(direction > 0 ? Send.Right : Send.Left);
        return direction > 0 ? "Next slide" : "Previous slide";
    }

    public string? Press()
    {
        Send.Chord(Send.F5);
        return "Start";
    }
}

/// <summary>One-shot: choosing it captures the whole screen (Win+PrtScn, saved to Pictures\\Screenshots).</summary>
internal sealed class ScreenshotMode : IMode
{
    public string Id => "screenshot";
    public string Label => "Screenshot";
    public string Short => "Shot";
    public void Exit() { }
    public string? Turn(int direction, bool fast) => null;
    public string? Press() => null;

    public void Enter()
    {
        // wait for the wheel to fade out so it isn't in the picture
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Send.Chord(Send.LWin, 0x2C /* PrintScreen */);
        };
        timer.Start();
    }
}
