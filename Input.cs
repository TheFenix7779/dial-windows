using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DialWin;

internal enum DialEventKind { Turn, Press, DoublePress, TriplePress }

internal readonly record struct DialEvent(DialEventKind Kind, int Direction = 0);

/// <summary>
/// Turns volume changes on the buds into rotary detents. In every mode except Volume the
/// volume is parked at a fixed level after each detent so the dial never hits 0 or 100%.
/// </summary>
internal sealed class VolumeEncoder : IDisposable, IMMNotificationClient
{
    const float EchoTolerance = 0.004f;
    const float DeadZone = 0.004f;

    readonly Settings _settings;
    readonly Dispatcher _ui;
    readonly Action<DialEvent> _emit;
    readonly MMDeviceEnumerator _enumerator = new();
    readonly object _gate = new();

    MMDevice? _device;
    float _last = -1;
    float? _expect;
    DateTime _expectUntil;
    float _saved = -1;
    bool _parked;
    bool _wantParking;

    public VolumeEncoder(Settings settings, Dispatcher ui, Action<DialEvent> emit)
    {
        _settings = settings;
        _ui = ui;
        _emit = emit;
    }

    // Park near the volume you already had, so opening a wheel doesn't make your audio jump louder or quieter.
    float _parkLevel = 0.5f;
    float Park => _parkLevel;

    /// <summary>The buds' current volume (0 to 1), or null when they aren't the output.</summary>
    public float? Current
    {
        get
        {
            try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar; }
            catch { return null; }
        }
    }

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        Log.Write("Output devices:");
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            Log.Write($"  - {d.FriendlyName}");
        Attach();
    }

    void Attach()
    {
        lock (_gate)
        {
            Detach();
            try
            {
                var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var name = dev.FriendlyName;
                bool match = string.IsNullOrEmpty(_settings.DeviceNameContains) ||
                             name.Contains(_settings.DeviceNameContains, StringComparison.OrdinalIgnoreCase);
                Log.Write($"Default output: {name} ({(match ? "listening" : "ignored, name doesn't match")})");
                if (!match) { dev.Dispose(); return; }

                _device = dev;
                _last = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                _parked = false;
                dev.AudioEndpointVolume.OnVolumeNotification += OnVolume;
                if (_wantParking) ApplyParking(true);
            }
            catch (Exception ex) { Log.Write("Attach failed: " + ex.Message); }
        }
    }

    void Detach()
    {
        if (_device == null) return;
        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
        _device.Dispose();
        _device = null;
    }

    /// <summary>Park the volume (every mode except Volume), restoring the old level afterwards.</summary>
    public void SetParking(bool parking)
    {
        lock (_gate)
        {
            _wantParking = parking;
            ApplyParking(parking);
        }
    }

    void ApplyParking(bool parking)
    {
        if (_device == null || parking == _parked) return;
        _parked = parking;
        if (parking)
        {
            _saved = _last >= 0 ? _last : Current ?? Park;
            _parkLevel = Math.Clamp(_saved, 0.2f, 0.8f);
            if (Math.Abs(_saved - _parkLevel) > 0.001f) WriteVolume(Park); // only when too close to an end
            _ui.BeginInvoke(OsdSuppressor.Pulse);
        }
        else if (_saved >= 0)
        {
            if (Math.Abs((Current ?? _saved) - _saved) > 0.001f) WriteVolume(_saved);
            _saved = -1;
        }
    }

    void WriteVolume(float value)
    {
        if (_device == null) return;
        try
        {
            _expect = value;
            _expectUntil = DateTime.UtcNow.AddMilliseconds(300);
            _last = value;
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = value;
        }
        catch (Exception ex) { Log.Write("Set volume failed: " + ex.Message); }
    }

    void OnVolume(AudioVolumeNotificationData data)
    {
        int direction;
        lock (_gate)
        {
            float v = data.MasterVolume;
            Log.Write($"volume {v:0.000} muted={data.Muted} parked={_parked}", verboseOnly: true);

            if (_expect is float e && DateTime.UtcNow < _expectUntil && Math.Abs(v - e) <= EchoTolerance)
            {
                _last = v;
                return; // our own write coming back
            }

            if (_parked)
            {
                // Follow the volume as it drifts, and only recentre near the ends. Every write we make
                // is another beep from the buds, so write as rarely as possible.
                float dv = v - _last;
                direction = dv > DeadZone ? 1 : dv < -DeadZone ? -1 : 0;
                _last = v;
                if (v < 0.15f || v > 0.85f) WriteVolume(Park);
            }
            else
            {
                direction = Math.Sign(v - _last);
                _last = v;
            }
        }

        if (direction != 0)
        {
            bool parked = _parked;
            _ui.BeginInvoke(() =>
            {
                if (parked) OsdSuppressor.Pulse();
                _emit(new DialEvent(DialEventKind.Turn, direction));
            });
        }
    }

    // IMMNotificationClient
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Attach();
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        lock (_gate)
        {
            if (_parked && _saved >= 0) WriteVolume(_saved);
            Detach();
        }
        _enumerator.Dispose();
    }
}

/// <summary>
/// Catches Play/Pause (press) and Next track (double press) before any media app sees them.
/// Keys we inject ourselves are let through.
/// </summary>
internal sealed class MediaKeyHook : IDisposable
{
    const uint NextTrack = 0xB0, PrevTrack = 0xB1, Stop = 0xB2, PlayPause = 0xB3;

    readonly Dispatcher _ui;
    readonly Action<DialEvent> _emit;
    readonly Native.LowLevelProc _proc;
    IntPtr _hook;

    public bool Enabled { get; set; } = true;

    public MediaKeyHook(Dispatcher ui, Action<DialEvent> emit)
    {
        _ui = ui;
        _emit = emit;
        _proc = Callback;
    }

    public void Start()
    {
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool media = k.vkCode is NextTrack or PrevTrack or Stop or PlayPause;
            bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;

            if (media && !injected)
            {
                bool down = wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN;
                if (down) Log.Write($"media key 0x{k.vkCode:X2}", verboseOnly: true);

                if (Enabled && (k.vkCode == PlayPause || k.vkCode == NextTrack || k.vkCode == PrevTrack))
                {
                    if (down)
                    {
                        var kind = k.vkCode == PlayPause ? DialEventKind.Press
                            : k.vkCode == NextTrack ? DialEventKind.DoublePress : DialEventKind.TriplePress;
                        _ui.BeginInvoke(() => _emit(new DialEvent(kind)));
                    }
                    return (IntPtr)1; // swallow, so the music app doesn't also react
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>Ctrl+Alt+Left/Right turn, Ctrl+Alt+Down presses, Ctrl+Alt+D double presses: develop without the buds.</summary>
internal sealed class SimulatedInput : IDisposable
{
    const uint ModAlt = 0x1, ModCtrl = 0x2, ModNoRepeat = 0x4000;

    readonly Action<DialEvent> _emit;
    HwndSource? _source;

    public SimulatedInput(Action<DialEvent> emit) => _emit = emit;

    public void Start()
    {
        var p = new HwndSourceParameters("DialHotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        uint mods = ModCtrl | ModAlt | ModNoRepeat;
        Register(1, mods, Send.Left);
        Register(2, mods, Send.Right);
        Register(3, mods, Send.Down);
        Register(4, mods, 0x44); // D
    }

    void Register(int id, uint mods, uint vk)
    {
        if (!Native.RegisterHotKey(_source!.Handle, id, mods, vk))
            Log.Write($"Hotkey {id} is taken by another app");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            switch (wParam.ToInt32())
            {
                case 1: _emit(new DialEvent(DialEventKind.Turn, -1)); break;
                case 2: _emit(new DialEvent(DialEventKind.Turn, 1)); break;
                case 3: _emit(new DialEvent(DialEventKind.Press)); break;
                case 4: _emit(new DialEvent(DialEventKind.DoublePress)); break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source == null) return;
        for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(_source.Handle, id);
        _source.Dispose();
        _source = null;
    }
}

/// <summary>
/// Registers as the current media player (System Media Transport Controls) with a silent loop,
/// so the buds' Play/Pause and Next track commands arrive here instead of a music app.
/// </summary>
internal sealed class MediaSessionSource : IDisposable
{
    readonly Dispatcher _ui;
    readonly Action<DialEvent> _emit;
    Windows.Media.Playback.MediaPlayer? _player;

    public MediaSessionSource(Dispatcher ui, Action<DialEvent> emit) { _ui = ui; _emit = emit; }

    Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? _manager;
    long _lastGrab;
    long _suspendUntil;

    public void Start()
    {
        Create();
        _ = Watch();
    }

    /// <summary>When another app (YouTube, Spotify) becomes the current media session, take it back.</summary>
    async System.Threading.Tasks.Task Watch()
    {
        try
        {
            _manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(CheckOwner);
        }
        catch (Exception ex) { Log.Write("Session watcher failed: " + ex.Message); }
    }

    async void CheckOwner()
    {
        try
        {
            var current = _manager?.GetCurrentSession();
            if (current == null) return;
            var props = await current.TryGetMediaPropertiesAsync();
            Log.Write($"current media session: {current.SourceAppUserModelId} \"{props.Title}\"", verboseOnly: true);
            if (props.Title == "Dial") return;

            long now = Environment.TickCount64;
            if (now < _suspendUntil || now - _lastGrab < 1500) return;
            _lastGrab = now;
            Log.Write("Another app took the media session, taking it back");
            Create();
        }
        catch (Exception ex) { Log.Write("CheckOwner failed: " + ex.Message); }
    }

    /// <summary>Play or pause the other app's media directly, without ever giving up our own session.</summary>
    public async void PassThrough()
    {
        try
        {
            if (_manager == null) return;
            var others = new System.Collections.Generic.List<Windows.Media.Control.GlobalSystemMediaTransportControlsSession>();
            foreach (var s in _manager.GetSessions())
                if (!s.SourceAppUserModelId.StartsWith("Dial", StringComparison.OrdinalIgnoreCase)) others.Add(s);

            var playing = others.Find(s => s.GetPlaybackInfo().PlaybackStatus ==
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            var target = playing ?? others.Find(s => s.GetPlaybackInfo().PlaybackStatus ==
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) ?? others.FirstOrDefault();
            if (target == null) { Log.Write("Pass-through: no other media app"); return; }

            bool ok = await target.TryTogglePlayPauseAsync();
            Log.Write($"Pass-through to {target.SourceAppUserModelId}: {ok}");
        }
        catch (Exception ex) { Log.Write("Pass-through failed: " + ex.Message); }
    }

    void Create()
    {
        try
        {
            _player?.Dispose();
            var player = new Windows.Media.Playback.MediaPlayer { IsLoopingEnabled = true };
            player.CommandManager.IsEnabled = false;
            var stream = new System.IO.MemoryStream(SilentWav());
            player.Source = Windows.Media.Core.MediaSource.CreateFromStream(
                System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(stream), "audio/wav");

            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = smtc.IsPauseEnabled = smtc.IsNextEnabled = smtc.IsPreviousEnabled = true;
            smtc.DisplayUpdater.Type = Windows.Media.MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = "Dial";
            smtc.DisplayUpdater.Update();
            smtc.ButtonPressed += (_, args) =>
            {
                Log.Write($"smtc button {args.Button}", verboseOnly: true);
                var kind = args.Button switch
                {
                    Windows.Media.SystemMediaTransportControlsButton.Play or
                    Windows.Media.SystemMediaTransportControlsButton.Pause => (DialEventKind?)DialEventKind.Press,
                    Windows.Media.SystemMediaTransportControlsButton.Next => DialEventKind.DoublePress,
                    Windows.Media.SystemMediaTransportControlsButton.Previous => DialEventKind.TriplePress,
                    _ => null,
                };
                if (kind is DialEventKind k) _ui.BeginInvoke(() => _emit(new DialEvent(k)));
                // stay "playing" so we keep getting the buttons
                smtc.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Playing;
            };
            player.Play();
            smtc.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Playing;
            _player = player;
            Log.Write("Registered as media session");
        }
        catch (Exception ex) { Log.Write("Media session failed: " + ex); }
    }

    static byte[] SilentWav()
    {
        const int rate = 8000, seconds = 2, dataLen = rate * seconds * 2;
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen); w.Write(new byte[dataLen]);
        return ms.ToArray();
    }

    public void Dispose() { _player?.Dispose(); _player = null; }
}
