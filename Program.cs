using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace DialWin;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "Dial.SingleInstance", out bool first);
        if (!first) return;

        Log.Init();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UNHANDLED " + e.Exception);
            e.Handled = true;
        };

        using var host = new DialHost(app);
        host.Start();
        app.Run();
    }
}

internal sealed class DialHost : IDisposable
{
    readonly Application _app;
    readonly Settings _settings = Settings.Load();
    readonly VolumeEncoder _encoder;
    readonly MediaKeyHook _keys;
    readonly SimulatedInput _sim;
    readonly MediaSessionSource _session;
    readonly long[] _lastPress = new long[4];
    readonly DialModel _model;
    readonly WheelWindow _wheel;
    readonly HudWindow _hud = new();
    readonly WF.NotifyIcon _tray = new();

    public DialHost(Application app)
    {
        _app = app;
        Log.Verbose = _settings.VerboseLog;
        var ui = app.Dispatcher;

        _encoder = new VolumeEncoder(_settings, ui, Dispatch);
        _keys = new MediaKeyHook(ui, Dispatch);
        _sim = new SimulatedInput(Dispatch);
        _session = new MediaSessionSource(ui, Dispatch);

        var modes = new List<IMode>
        {
            new VolumeMode(_encoder, _settings), new BrightnessMode(), new AppsMode(),
            new TaskViewMode(), new DesktopsMode(), new TabsMode(),
            new ScrollMode(), new UndoRedoMode(), new SlidesMode(), new ScreenshotMode(),
        };
        _model = new DialModel(modes, _encoder.SetParking, _session.PassThrough);
        _wheel = new WheelWindow(_model);
        _model.Hud += _hud.ShowText;
    }

    void Dispatch(DialEvent e)
    {
        if (e.Kind != DialEventKind.Turn)
        {
            // the key hook and the media session can both report the same press
            long now = Environment.TickCount64;
            if (now - _lastPress[(int)e.Kind] < 250) return;
            _lastPress[(int)e.Kind] = now;
        }
        Log.Write($"event {e.Kind} {e.Direction}", verboseOnly: true);
        _model.Handle(e);
    }

    public void Start()
    {
        Log.Write("Dial started");
        BuildTray();
        Startup.Apply(_settings.StartWithWindows);
        _keys.Start();
        _sim.Start();
        _session.Start();
        _encoder.Start();
        _model.Start();
    }

    void BuildTray()
    {
        _tray.Icon = MakeIcon();
        _tray.Text = "Dial";
        _tray.Visible = true;

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open wheel (Ctrl+Alt+D)", null, (_, _) => Dispatch(new DialEvent(DialEventKind.DoublePress)));

        var pause = new WF.ToolStripMenuItem("Pause Dial") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _keys.Enabled = !pause.Checked;
        menu.Items.Add(pause);

        var startup = new WF.ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startup.Checked;
            _settings.Save();
            Startup.Apply(startup.Checked);
        };
        menu.Items.Add(startup);

        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open log", null, (_, _) => Open(Log.FilePath));
        menu.Items.Add("Open settings", null, (_, _) => Open(System.IO.Path.Combine(Log.Dir, "settings.json")));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _app.Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Couldn't open " + path + ": " + ex.Message); }
    }

    static SD.Icon MakeIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var ring = new SD.Pen(SD.Color.White, 3.5f);
            g.DrawEllipse(ring, 3, 3, 25, 25);
            using var dot = new SD.SolidBrush(SD.Color.FromArgb(0xE8, 0x11, 0x2D));
            g.FillEllipse(dot, 13, 5, 6, 6);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _sim.Dispose();
        _keys.Dispose();
        _session.Dispose();
        _encoder.Dispose();
        Log.Write("Dial stopped");
    }
}
