using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace DialWin;

internal enum Phase { InMode, Picking, AppPicker }

/// <summary>
/// The state machine. Double press opens the wheel, turning picks, press chooses.
/// Outside the wheel, every turn goes to the active mode.
/// </summary>
internal sealed class DialModel
{
    const long FastSpinMs = 120;

    readonly Action<bool> _setParking;
    readonly Action _passThroughPress;
    List<WheelItem> _apps = new();
    long _lastTurnTicks;
    readonly DispatcherTimer _dwell = new() { Interval = TimeSpan.FromMilliseconds(1000) };

    public DialModel(IReadOnlyList<IMode> modes, Action<bool> setParking, Action passThroughPress)
    {
        _passThroughPress = passThroughPress;
        WireDwell();
        Modes = modes;
        _setParking = setParking;
    }

    public IReadOnlyList<IMode> Modes { get; }
    public int ActiveIndex { get; private set; }
    public int Selection { get; private set; }
    public Phase Phase { get; private set; } = Phase.InMode;
    public IMode Active => Modes[ActiveIndex];

    /// <summary>What the wheel currently shows: the modes, or the open windows.</summary>
    public IReadOnlyList<WheelItem> WheelItems => Phase == Phase.AppPicker
        ? _apps
        : Modes.Select(m => new WheelItem(m.Label, m.Short, null)).ToList();

    public event Action? Changed;
    public event Action? DwellChanged;
    public int DwellMs => (int)_dwell.Interval.TotalMilliseconds;
    public bool Dwelling => _dwell.IsEnabled;
    public event Action<string, string>? Hud;

    void UpdateParking() => _setParking(Phase == Phase.Picking || Active.Id != "volume");

    /// <summary>In a wheel, holding still after a turn selects, so no click is needed.</summary>
    void WireDwell() => _dwell.Tick += (_, _) =>
    {
        _dwell.Stop();
        if (Phase != Phase.InMode) Handle(new DialEvent(DialEventKind.Press));
    };

    public void Handle(DialEvent e)
    {
        _dwell.Stop();
        HandleCore(e);
        if (e.Kind == DialEventKind.Turn && Phase != Phase.InMode) _dwell.Start();
        DwellChanged?.Invoke();
    }

    void HandleCore(DialEvent e)
    {
        if (e.Kind == DialEventKind.TriplePress) { GoBack(); return; }
        switch (Phase)
        {
            case Phase.Picking: HandlePicking(e); break;
            case Phase.AppPicker: HandleAppPicker(e); break;
            default: HandleInMode(e); break;
        }
    }

    void HandleInMode(DialEvent e)
    {
        switch (e.Kind)
        {
            case DialEventKind.Turn:
                long now = Environment.TickCount64;
                bool fast = now - _lastTurnTicks < FastSpinMs;
                _lastTurnTicks = now;
                Report(Active.Turn(e.Direction, fast));
                break;
            case DialEventKind.Press:
                if (!Active.OwnsPress) _passThroughPress();
                else if (Active.Id == "apps") OpenAppPicker();
                else Report(Active.Press());
                break;
            case DialEventKind.DoublePress: // open the mode wheel
                Active.Exit();
                Selection = ActiveIndex;
                Phase = Phase.Picking;
                UpdateParking();
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Apps is a one-shot: after the wheel, a click plays or pauses again.</summary>
    void BackToVolume()
    {
        ActiveIndex = 0;
        Phase = Phase.InMode;
        UpdateParking();
        Changed?.Invoke();
    }

    /// <summary>Triple tap: close any wheel and return to Volume mode.</summary>
    void GoBack()
    {
        if (Phase == Phase.InMode && ActiveIndex == 0) return;
        Active.Exit();
        BackToVolume();
    }

    void OpenAppPicker()
    {
        _apps = AppWindows.List();
        Selection = 0;
        Phase = Phase.AppPicker;
        UpdateParking();
        Changed?.Invoke();
    }

    void HandleAppPicker(DialEvent e)
    {
        switch (e.Kind)
        {
            case DialEventKind.Turn:
                if (_apps.Count > 0) Selection = (Selection + e.Direction + _apps.Count) % _apps.Count;
                Changed?.Invoke();
                break;
            case DialEventKind.Press:
                BackToVolume();
                if (_apps.Count > 0) AppWindows.Activate(_apps[Selection].Window);
                break;
            case DialEventKind.DoublePress: // cancel
                BackToVolume();
                break;
        }
    }

    void HandlePicking(DialEvent e)
    {
        switch (e.Kind)
        {
            case DialEventKind.Turn:
                Selection = (Selection + e.Direction + Modes.Count) % Modes.Count;
                Changed?.Invoke();
                break;
            case DialEventKind.Press:
                ActiveIndex = Selection;
                Resume();
                Hud?.Invoke(Active.Label, "");
                break;
            case DialEventKind.DoublePress: // cancel, keep the current mode
                Resume();
                break;
        }
    }

    void Resume()
    {
        Phase = Phase.InMode;
        UpdateParking();
        Active.Enter();
        if (Active.Id == "screenshot")
        {
            BackToVolume();
            var note = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            note.Tick += (_, _) =>
            {
                note.Stop();
                Hud?.Invoke("Screenshot", "Saved to Pictures\\Screenshots");
            };
            note.Start();
            return;
        }
        if (Active.Id == "apps") { OpenAppPicker(); return; }
        Changed?.Invoke();
    }

    void Report(string? text)
    {
        if (text != null) Hud?.Invoke(Active.Label, text);
    }

    /// <summary>Call once at start so volume is parked correctly for the first mode.</summary>
    public void Start() => UpdateParking();
}
